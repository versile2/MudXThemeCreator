# Focused PostgreSQL startup checks (net9)

The legacy `MudBlazorThemes.UnitTest` references absent UI/DAL projects. This small executable harness uses the application's existing dependencies, no additional packages or SDK installations. Its nonzero exit is the test verdict; it is not a `dotnet test` discovery project.

```sh
export DOTNET_ROOT=/home/versile/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet "$DOTNET_ROOT/sdk/9.0.314/dotnet.dll" run --project tests/PostgresGuard.Tests -f net9.0
python3 tests/PostgresGuard.Tests/test_entrypoint.py
python3 tests/PostgresGuard.Tests/test_app_process.py
```

C# uses real Npgsql against minimal loopback wire peers: authentication/error messages, extended SELECT protocol, malformed result, cancellation, connection disposal and deterministic EF provider registration. Python runs the real POSIX shell against controlled dotnet/sleep executables to observe six attempts and five virtual 60-second waits, execution order, exit statuses, signals and `exec` PID preservation. Process tests run the actual built net9 app and shell against isolated peers, observe socket closure and refusal of the configured HTTP listener, deadlines, shutdown, and guard-success/application-auth-failure race.

These are offline protocol/process tests, not real PostgreSQL, image, schema, TLS certificate-chain or production acceptance. No test invokes app initialization/migration. Full successful web-host boot is deliberately not exercised, because the existing service invokes automatic migration. A production activation needs independent model/schema/migration compatibility evidence.
