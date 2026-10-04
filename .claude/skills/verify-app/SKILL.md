---
name: verify-app
description: Builds the app, runs the unit tests, and smoke-tests that the window really starts, without disturbing any copy of the app that is already running. Use when the user asks to verify, sanity-check, or smoke-test the app after changes.
disable-model-invocation: true
argument-hint: [fake|live]
---

# Verify the app

Check three things, in order, and report each one separately: **it compiles, the tests pass, the
window starts.** Use the simulated feed unless the user passed `live` as an argument, because the live
feed needs internet access.

## Safety rules (read first)

- **Never stop a process by name.** The user may have their own copy of the app open. Only ever stop
  the process you started yourself, by its PID.
- A running copy of the app locks `bin\Debug\...\TradingApp.exe`, which makes builds that write there
  fail with file-lock errors (MSB3021 / MSB3027). If you see one, tell the user to close their copy and
  re-run. Do not kill it for them.
- Build to a temporary folder for the compile check, so a running copy cannot get in the way.

## Steps (PowerShell, from the repository root)

1. **See what is already running.**
   ```powershell
   Get-Process TradingApp -ErrorAction SilentlyContinue | Select-Object Id, StartTime, Path
   ```
   Note the PIDs. These are not yours.

2. **Compile check** into a temp folder:
   ```powershell
   dotnet build TradingApp.csproj --nologo -v q -o $env:TEMP\tradingapp_verify
   ```
   Report the warning and error counts. If it fails, show the errors and stop.

3. **Unit tests:**
   ```powershell
   dotnet test tests/TradingApp.Tests --nologo -v q
   ```
   Report passed / failed / total. This builds the app into `bin\Debug`, so if a copy is running and the
   build hits a file lock, say so and ask the user to close it. Do not work around it by killing the process.

4. **Smoke test.** Start the temp build and keep the process object so you only stop your own:
   ```powershell
   $env:TRADINGAPP_FEED = "fake"      # omit this line when the user asked for live
   $app = Start-Process "$env:TEMP\tradingapp_verify\TradingApp.exe" -PassThru
   Start-Sleep -Seconds 12
   $app.Refresh()
   "exited: $($app.HasExited); title: '$($app.MainWindowTitle)'"
   if (-not $app.HasExited) { Stop-Process -Id $app.Id }     # only the PID you started
   Remove-Item Env:TRADINGAPP_FEED -ErrorAction SilentlyContinue
   ```
   Pass means: it did not exit early, and the window title is `Trading Practice`. If it exited, report the
   exit code.

## Report

One line per check (compile, tests, smoke), each marked passed or failed, with counts. Be exact about
the limits: the smoke test shows the window opens and the app survives ~12 seconds. It does **not**
verify what is drawn or that data is flowing. Say so, and offer to take a screenshot if the user wants
a visual check.
