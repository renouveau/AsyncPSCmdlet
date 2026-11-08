// SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
// SPDX-License-Identifier: MIT

// This file is part of Renouveau AsyncPSCmdlet, available under the MIT license.
// https://github.com/renouveau/AsyncPSCmdlet

using System;
using System.Management.Automation;
using System.Threading.Tasks;

#pragma warning disable CA2007 // No synchronization context is on the thread pool.
#pragma warning disable CA1303 // Don't police me on internationalization.

namespace Renouveau.ForPowerShell
{
  /// <summary>
  /// Exemplifies <c>Write...</c> methods.
  /// </summary>
  [Cmdlet(VerbsLifecycle.Invoke, "Write")]
  public sealed class InvokeWrite : AsyncPSCmdlet
  {
    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
      base.BeginProcessing();
      SendToThreadPool(async dispatcher =>
      {
        // Verify that `WriteObject` accepts `null`.
        await Task.Delay(1000);
        dispatcher.WriteObjectAndForget(null);
        dispatcher.WriteObjectAndForget(null, false);
        dispatcher.WriteObjectAndForget(null, true);
        await Task.Delay(1000);
        await dispatcher.WriteObjectAsync(null);
        await dispatcher.WriteObjectAsync(null, false);
        await dispatcher.WriteObjectAsync(null, true);
        await Task.Delay(1000);
        DateTime[] dt = [DateTime.Now, DateTime.UtcNow];
        dispatcher.WriteObjectAndForget(dt);
        dispatcher.WriteObjectAndForget(dt, false);
        dispatcher.WriteObjectAndForget(dt, true);
        await Task.Delay(1000);
        dispatcher.WriteObjectAndForget(dt);
        dispatcher.WriteObjectAndForget(dt, false);
        dispatcher.WriteObjectAndForget(dt, true);
      });
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
