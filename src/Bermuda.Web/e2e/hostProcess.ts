import { spawn } from 'node:child_process'
import { existsSync, statSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

// The key the Aspire host gives the app under test (AppHost.cs: SINGLE_INSTANCE_KEY). A second process must use
// the same one to find it.
const SINGLE_INSTANCE_KEY = 'Bermuda.AppHost'

/** The Bermuda.exe Aspire started: BERMUDA_EXE, else the newest build of the host project. */
function findHostExe(): string {
  if (process.env.BERMUDA_EXE) return process.env.BERMUDA_EXE
  const candidates = ['Debug', 'Release']
    .map((config) => resolve(dirname(fileURLToPath(import.meta.url)), `../../Bermuda/bin/${config}/net10.0-windows10.0.19041.0/Bermuda.exe`))
    .filter(existsSync)
    .sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)
  if (candidates.length === 0) throw new Error('Bermuda.exe not found; build src/Bermuda or set BERMUDA_EXE.')
  return candidates[0]
}

/** Starts a second Bermuda process and resolves with its exit code (it must exit by itself). */
export function launchSecondInstance(args: string[]): Promise<number | null> {
  return new Promise((resolveExit, reject) => {
    const child = spawn(findHostExe(), args, {
      env: { ...process.env, SINGLE_INSTANCE_KEY, FDC3_GRPC_ENABLED: 'false', NATIVE_APP_AUTOSTART: 'false' },
      stdio: 'ignore',
    })
    const timer = setTimeout(() => {
      child.kill()
      reject(new Error('The second Bermuda did not exit: it did not hand over to the running instance.'))
    }, 20_000)
    child.once('error', reject)
    child.once('exit', (code) => {
      clearTimeout(timer)
      resolveExit(code)
    })
  })
}
