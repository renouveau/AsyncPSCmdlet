// SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
// SPDX-License-Identifier: MIT

// This file is part of Renouveau AsyncPSCmdlet, available under the MIT license.
// https://github.com/renouveau/AsyncPSCmdlet

using System.Management.Automation;

#pragma warning disable CA2007 // No synchronization context is on the thread pool.
#pragma warning disable CA1303 // Don't police me on internationalization.

namespace Renouveau.ForPowerShell
{
  /// <summary>
  /// Shows that <c>SendToThreadPool</c> blocks invocation from wrong threads.
  /// </summary>
  [Cmdlet(VerbsLifecycle.Invoke, "Should", SupportsShouldProcess = true)]
  public sealed class InvokeShould : AsyncPSCmdlet
  {
    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
      base.BeginProcessing();
      SendToThreadPool(async dispatcher =>
      {
        string msg;
        ShouldContinueProcessBox box = new ShouldContinueProcessBox();
        // ShouldContinue
        msg = "ShouldContinue2 returned ";
        msg += await dispatcher.ShouldContinueAsync("query", "caption");
        dispatcher.WriteObjectAndForget(msg);
        msg = "ShouldContinue4 returned ";
        msg += await dispatcher.ShouldContinueAsync("query", "caption", box);
        msg += " and set yesToAll = " + box.YesToAll + ", noToAll = " + box.NoToAll;
        dispatcher.WriteObjectAndForget(msg);
        msg = "ShouldContinue5 returned ";
        msg += await dispatcher.ShouldContinueAsync("query", "caption", true, box);
        msg += " and set yesToAll = " + box.YesToAll + ", noToAll = " + box.NoToAll;
        dispatcher.WriteObjectAndForget(msg);
        // ShouldProcess
        msg = "ShouldProcess1 returned ";
        msg += await dispatcher.ShouldProcessAsync("target");
        dispatcher.WriteObjectAndForget(msg);
        msg = "ShouldProcess2 returned ";
        msg += await dispatcher.ShouldProcessAsync("target", "action");
        dispatcher.WriteObjectAndForget(msg);
        msg = "ShouldProcess3 returned ";
        msg += await dispatcher.ShouldProcessAsync("description", "warning", "caption");
        dispatcher.WriteObjectAndForget(msg);
        msg = "ShouldProcess4 returned ";
        msg += await dispatcher.ShouldProcessAsync("description", "warning", "caption", box);
        msg += " and set shouldProcessReason = " + box.ShouldProcessReason;
        dispatcher.WriteObjectAndForget(msg);
      });
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
