// Copyright (C) 2026 DNT_OF
// SPDX-License-Identifier: Apache-2.0
// Original project: https://github.com/DNTOF/SLDataAPI

using System;
using System.Collections;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SLDataAPI.Auth;

namespace SLDataAPI.Services;

/// <summary>
/// 自签 TLS 证书管理（contract v1）：
///   - 首次启动在插件配置目录（configs/&lt;端口&gt;/SLDataAPI/）生成 RSA-2048 / SHA-256 自签证书，
///     CN=SLDataAPI，有效期 10 年；存为 <c>sld_tls.pfx</c>，随机密码存 <c>sld_tls.pwd</c>（尽力收紧 ACL）。
///   - 生成：优先 Mono.Security（X509CertificateBuilder + PKCS12，Unity Mono 自带），
///     失败回退 BouncyCastle（游戏自带 BouncyCastle.Cryptography；必须 DER 编码 + 3DES，
///     否则 Mono.Security 的 PKCS12 解析器不认 BER 不定长编码）。
///   - 不使用 CertificateRequest：net48 编译引用指向 System.Core，Unity Mono 里无类型转发（TypeLoadException），
///     反射调用 CreateSelfSigned 也是 PlatformNotSupported。
///   - regen 热替换：新连接用新证书，已建立的连接不受影响。
/// 指纹 = SHA-256(证书 DER)，大写十六进制冒号分隔（见 <see cref="TlsPolicy.ComputeFingerprint"/>）。
/// </summary>
public static class TlsCertificateService
{
    public const string PfxFileName = "sld_tls.pfx";
    public const string PasswordFileName = "sld_tls.pwd";
    public const string SubjectName = "CN=SLDataAPI";
    private const int KeySize = 2048;
    private const int ValidityYears = 10;

    private static readonly object Gate = new();
    private static volatile X509Certificate2? _cert;
    private static volatile string? _fingerprint;
    private static volatile TlsMode _mode = TlsMode.Off;
    private static string _dir = "";

    /// <summary>当前生效的 TLS 模式（Init 后；证书不可用时 optional 会降级为 off）。</summary>
    public static TlsMode Mode => _mode;

    /// <summary>当前证书（off 或尚未就绪时为 null）。读取是原子的引用读，可在任意线程调用。</summary>
    public static X509Certificate2? Current => _cert;

    /// <summary>当前证书指纹（off 时为 null）。</summary>
    public static string? Fingerprint => _fingerprint;

    /// <summary>证书最近一次由谁生成/加载（mono.security / bouncycastle / loaded）。</summary>
    public static string Source { get; private set; } = "";

    public static string PfxPath => string.IsNullOrEmpty(_dir) ? "" : Path.Combine(_dir, PfxFileName);
    public static string PasswordPath => string.IsNullOrEmpty(_dir) ? "" : Path.Combine(_dir, PasswordFileName);

    /// <summary>由 Plugin.Enable 在启动 HTTP / 语音监听之前调用。返回启动横幅文本。</summary>
    public static string Init(string? configuredMode, string configDir)
    {
        TlsMode mode = TlsPolicy.ParseMode(configuredMode, out bool valid);
        if (!valid)
            Log.Warn($"[SLDataAPI] tls_mode 值非法: \"{configuredMode}\"（仅支持 off / optional / required），本次运行按 optional 处理。");

        _cert = null;
        _fingerprint = null;
        Source = "";

        if (string.IsNullOrWhiteSpace(configDir))
            configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SCP Secret Laboratory", "SLDataAPI");
        _dir = configDir;

        if (mode == TlsMode.Off)
        {
            _mode = TlsMode.Off;
            return "TLS: off";
        }

        try
        {
            lock (Gate) LoadOrCreateUnlocked();
            _mode = mode;
        }
        catch (Exception ex)
        {
            // optional：降级为纯明文继续服务；required：保持 required（明文 426、TLS 握手无证书直接断开），fail-closed
            _mode = mode == TlsMode.Required ? TlsMode.Required : TlsMode.Off;
            Log.Error($"[SLDataAPI] TLS 证书加载/生成失败：{ex.GetType().Name}: {ex.Message}" +
                      (mode == TlsMode.Required ? " —— tls_mode=required，所有连接将被拒绝（fail-closed）" : " —— 本次按 off 运行（仅明文）"));
        }

        return Banner();
    }

    /// <summary>启动横幅：<c>TLS: &lt;mode&gt;, SHA-256 &lt;fingerprint&gt;</c>。</summary>
    public static string Banner()
    {
        string m = TlsPolicy.ToConfigString(_mode);
        string? fp = _fingerprint;
        return fp == null ? $"TLS: {m}" : $"TLS: {m}, SHA-256 {fp}";
    }

    /// <summary>
    /// 重新生成证书并热替换：新连接立即使用新证书，已建立的连接保持不变。
    /// 已固定旧指纹的客户端必须重新确认新指纹。
    /// </summary>
    public static bool TryRegenerate(out string oldFingerprint, out string newFingerprint, out string error)
    {
        oldFingerprint = _fingerprint ?? "";
        newFingerprint = "";
        error = "";
        if (_mode == TlsMode.Off)
        {
            error = "tls_mode 为 off：未启用 TLS，无证书可重新生成（改为 optional / required 并重启后生效）";
            return false;
        }

        lock (Gate)
        {
            try
            {
                var cert = GenerateAndPersistUnlocked();
                newFingerprint = _fingerprint ?? "";
                return cert != null;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}（旧证书保持生效）";
                return false;
            }
        }
    }

    // ────────────── 加载 / 生成 ──────────────

    private static void LoadOrCreateUnlocked()
    {
        Directory.CreateDirectory(_dir);
        string pfx = PfxPath, pwd = PasswordPath;
        if (File.Exists(pfx) && File.Exists(pwd))
        {
            try
            {
                string password = File.ReadAllText(pwd).Trim();
                var cert = new X509Certificate2(File.ReadAllBytes(pfx), password, X509KeyStorageFlags.Exportable);
                if (!cert.HasPrivateKey)
                    throw new CryptographicException("证书缺少私钥");
                Activate(cert, "loaded");
                ApiKeyCreateDelivery.TryTightenOnceFileAcl(pwd);
                return;
            }
            catch (Exception ex)
            {
                // 文件损坏 / 密码不匹配：留档后重新生成（指纹会变化，客户端需重新确认）
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Move(pfx, pfx + ".bad-" + stamp); } catch { /* 忽略 */ }
                try { File.Move(pwd, pwd + ".bad-" + stamp); } catch { /* 忽略 */ }
                Log.Warn($"[SLDataAPI] 现有 TLS 证书无法加载（{ex.GetType().Name}: {ex.Message}），已改名留档并重新生成。" +
                         "指纹将变化：已固定旧指纹的客户端需要重新确认。");
            }
        }
        GenerateAndPersistUnlocked();
    }

    private static X509Certificate2 GenerateAndPersistUnlocked()
    {
        Directory.CreateDirectory(_dir);
        string password = NewPassword();
        byte[] pfxBytes;
        string source;
        try
        {
            pfxBytes = GenerateWithMonoSecurity(password);
            source = "mono.security";
        }
        catch (Exception monoEx)
        {
            Log.Debug($"[SLDataAPI] Mono.Security 生成证书失败，回退 BouncyCastle: {monoEx.GetType().Name}: {monoEx.Message}");
            pfxBytes = GenerateWithBouncyCastle(password);
            source = "bouncycastle";
        }

        // 先确认能用本运行时加载（含私钥），再落盘、再切换
        var cert = new X509Certificate2(pfxBytes, password, X509KeyStorageFlags.Exportable);
        if (!cert.HasPrivateKey)
            throw new CryptographicException($"{source} 生成的 PFX 加载后缺少私钥");

        WriteAtomic(PasswordPath, System.Text.Encoding.ASCII.GetBytes(password));
        ApiKeyCreateDelivery.TryTightenOnceFileAcl(PasswordPath);
        WriteAtomic(PfxPath, pfxBytes);
        ApiKeyCreateDelivery.TryTightenOnceFileAcl(PfxPath);

        Activate(cert, source);
        Log.Info($"[SLDataAPI] 已生成自签 TLS 证书（{source}，RSA-{KeySize}，{SubjectName}，{ValidityYears} 年）：{PfxPath}");
        return cert;
    }

    private static void Activate(X509Certificate2 cert, string source)
    {
        // 旧证书不 Dispose：可能仍有握手在用它，交给 GC
        _fingerprint = TlsPolicy.ComputeFingerprint(cert.RawData);
        _cert = cert;
        Source = source;
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private static string NewPassword()
    {
        byte[] b = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] NewSerial()
    {
        byte[] serial = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(serial);
        serial[0] &= 0x7F; // 正数
        if (serial[0] == 0) serial[0] = 1;
        return serial;
    }

    /// <summary>Mono.Security（Unity Mono 自带，Windows / Linux 服务端均可用；.NET Framework 上不可用）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] GenerateWithMonoSecurity(string password)
    {
        var rsa = new RSACryptoServiceProvider(KeySize);
        var b = new Mono.Security.X509.X509CertificateBuilder(3)
        {
            SerialNumber = NewSerial(),
            IssuerName = SubjectName,
            SubjectName = SubjectName,
            NotBefore = DateTime.UtcNow.AddDays(-1),
            NotAfter = DateTime.UtcNow.AddYears(ValidityYears),
            SubjectPublicKey = rsa,
            Hash = "SHA256",
        };
        b.Extensions.Add(new Mono.Security.X509.Extensions.BasicConstraintsExtension { CertificateAuthority = false });
        var eku = new Mono.Security.X509.Extensions.ExtendedKeyUsageExtension();
        eku.KeyPurpose.Add("1.3.6.1.5.5.7.3.1"); // serverAuth
        b.Extensions.Add(eku);
        byte[] der = b.Sign(rsa);

        var p12 = new Mono.Security.X509.PKCS12 { Password = password };
        var attrs = new Hashtable();
        var keyId = new ArrayList { new byte[] { 1, 0, 0, 0 } };
        attrs.Add("1.2.840.113549.1.9.21", keyId); // PKCS#9 localKeyId：关联证书与私钥
        p12.AddCertificate(new Mono.Security.X509.X509Certificate(der), attrs);
        p12.AddPkcs8ShroudedKeyBag(rsa, attrs);
        return p12.GetBytes();
    }

    /// <summary>BouncyCastle 回退（游戏 Managed 自带 BouncyCastle.Cryptography）。必须 DER + 3DES。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] GenerateWithBouncyCastle(string password)
    {
        var rnd = new Org.BouncyCastle.Security.SecureRandom();
        var kpg = new Org.BouncyCastle.Crypto.Generators.RsaKeyPairGenerator();
        kpg.Init(new Org.BouncyCastle.Crypto.KeyGenerationParameters(rnd, KeySize));
        var kp = kpg.GenerateKeyPair();

        var g = new Org.BouncyCastle.X509.X509V3CertificateGenerator();
        var dn = new Org.BouncyCastle.Asn1.X509.X509Name(SubjectName);
        g.SetSerialNumber(new Org.BouncyCastle.Math.BigInteger(1, NewSerial()));
        g.SetIssuerDN(dn);
        g.SetSubjectDN(dn);
        g.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        g.SetNotAfter(DateTime.UtcNow.AddYears(ValidityYears));
        g.SetPublicKey(kp.Public);
        g.AddExtension(Org.BouncyCastle.Asn1.X509.X509Extensions.BasicConstraints, true,
            new Org.BouncyCastle.Asn1.X509.BasicConstraints(false));
        g.AddExtension(Org.BouncyCastle.Asn1.X509.X509Extensions.ExtendedKeyUsage, false,
            new Org.BouncyCastle.Asn1.X509.ExtendedKeyUsage(Org.BouncyCastle.Asn1.X509.KeyPurposeID.id_kp_serverAuth));
        var cert = g.Generate(new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory("SHA256WITHRSA", kp.Private, rnd));

        var tdes = Org.BouncyCastle.Asn1.Pkcs.PkcsObjectIdentifiers.PbeWithShaAnd3KeyTripleDesCbc;
        var store = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder()
            .SetCertAlgorithm(tdes)
            .SetKeyAlgorithm(tdes)
            .SetUseDerEncoding(true)
            .Build();
        var entry = new Org.BouncyCastle.Pkcs.X509CertificateEntry(cert);
        store.SetCertificateEntry("sldataapi", entry);
        store.SetKeyEntry("sldataapi", new Org.BouncyCastle.Pkcs.AsymmetricKeyEntry(kp.Private), new[] { entry });
        using var ms = new MemoryStream();
        store.Save(ms, password.ToCharArray(), rnd);
        return ms.ToArray();
    }
}

/// <summary>同端口 TLS 嗅探 / 握手的传输层辅助（HttpServer 与语音口共用）。</summary>
public static class TlsTransport
{
    /// <summary>TLS 握手最长时间（毫秒）：防止慢速握手占用连接。</summary>
    public const int HandshakeTimeoutMs = 10000;

    /// <summary>
    /// 阻塞等待并窥视首字节（不消费）。返回 0..255；-1 = 超时无数据；-2 = 对端已关闭 / 出错。
    /// </summary>
    public static int PeekFirstByte(Socket socket, int timeoutMs)
    {
        try
        {
            if (!socket.Poll(Math.Max(1, timeoutMs) * 1000, SelectMode.SelectRead))
                return -1;
            byte[] b = new byte[1];
            int n = socket.Receive(b, 0, 1, SocketFlags.Peek);
            return n <= 0 ? -2 : b[0];
        }
        catch
        {
            return -2;
        }
    }

    /// <summary>
    /// 以服务端身份完成 TLS 握手（TLS 1.2 | 1.3，不要求客户端证书），超过 timeoutMs 视为失败。
    /// 失败返回 null（调用方关闭连接）。
    /// </summary>
    public static SslStream? TryAuthenticateServer(Stream inner, X509Certificate2 cert, int timeoutMs, out string error)
    {
        error = "";
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            var task = ssl.AuthenticateAsServerAsync(cert, false, TlsPolicy.ServerProtocols, false);
            if (!task.Wait(Math.Max(1000, timeoutMs)))
            {
                // 观察迟到的异常，避免 UnobservedTaskException 噪音
                task.ContinueWith(t => { _ = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                error = "TLS 握手超时";
                try { ssl.Dispose(); } catch { /* 忽略 */ }
                return null;
            }
            return ssl;
        }
        catch (Exception ex)
        {
            Exception e = ex is AggregateException ae && ae.InnerException != null ? ae.InnerException : ex;
            error = $"TLS 握手失败: {e.GetType().Name}: {e.Message}";
            try { ssl.Dispose(); } catch { /* 忽略 */ }
            return null;
        }
    }
}
