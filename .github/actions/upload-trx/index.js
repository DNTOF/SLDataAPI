const { spawnSync } = require('child_process')

// The results token is injected for JavaScript actions, not for plain run steps.
process.env.SUITE = process.env.INPUT_SUITE || ''
const result = spawnSync('python3', ['.github/scripts/upload-trx.py'], {
  stdio: 'inherit',
  env: process.env,
})
if (result.error) {
  console.error(result.error.message)
  process.exit(1)
}
process.exit(result.status === null ? 1 : result.status)
