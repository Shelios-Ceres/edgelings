## Summary

- 

## Verification

- [ ] Windows: `dotnet run --project windows/tests/AgentIsland.Tests -c Release` ends with `ALL GREEN`
- [ ] macOS (if `Sources/` changed): `./scripts/verify.sh`
- [ ] Tests don't depend on the machine's language, time zone or real agent folders
- [ ] Commits are Conventional Commits with no `Co-Authored-By` line

## Notes

Mention any UI change, new file the app reads or writes, or new network call.
