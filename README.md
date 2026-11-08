# Renouveau AsyncPSCmdlet

`AsyncPSCmdlet` is a single-file library for creating PowerShell cmdlets that call into asynchronous code.

To use this library,

1. copy [`AsyncPSCmdlet.cs`](AsyncPSCmdlet.cs) to your project,
2. modify [the namespace](AsyncPSCmdlet.cs#L23), and
3. modify [the version-tracking comments of `AsyncPSCmdlet` class](AsyncPSCmdlet.cs#L28-L36).

## change log

**v0.2.0.**

- Fixed PowerShell reference assemblies package referencing in example project.
- Minor improvement on documentation.
- Added `Should(Continue/Process)Async` methods (no fire-and-forget as their return type is not `void`).

**v0.1.0.**  Initial release supporting

- execution on thread pool, and
- communication back to the cmdlet’s main thread using `(Invoke/WriteXxx/ThrowTerminatingError)(AndForget/Async)` methods (fire-and-forget and awaitable).
