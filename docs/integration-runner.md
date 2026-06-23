# Integration Test Runner — W10C_DOS-ARCH_Testing

The `integration` workflow runs against a self-hosted Windows runner on
**W10C_DOS-ARCH_Testing**. The runner is not a persistent service — it must
be started manually before triggering a run.

## Starting the runner

On the VM, open PowerShell and run:

```powershell
cd C:\Windows\system32\actions-runner
.\run.cmd
```

Wait for `Listening for Jobs` to appear. Leave the window open for the
duration of the test run.

## Triggering a run

1. Go to **Actions → integration → Run workflow**
2. Approve the **integration-vm** environment gate when prompted
3. Watch the run — close the `run.cmd` window when it finishes

## Stopping the runner

Close the `run.cmd` window, or Ctrl+C in it.
