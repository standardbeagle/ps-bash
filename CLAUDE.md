# ps-bash project instructions

## Running Tests

Always use `scripts/test.sh` instead of `dotnet test` directly.
It runs the test suite and shuts down MSBuild server nodes and testhost processes on exit,
preventing zombie process accumulation.

```bash
# Run all tests
./scripts/test.sh

# Run with args (e.g. specific project or filter)
./scripts/test.sh src/PsBash.Core.Tests
./scripts/test.sh --filter "MyTest"
./scripts/test.sh --no-build
```

Do NOT use bare `dotnet test ...` — it leaks MSBuild worker nodes and testhost processes.
