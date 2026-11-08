// SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
// SPDX-License-Identifier: MIT

// This file is part of Renouveau AsyncPSCmdlet, available under the MIT license.
// https://github.com/renouveau/AsyncPSCmdlet

using System;
using System.Management.Automation;

#pragma warning disable CA2007 // No synchronization context is on the thread pool.
#pragma warning disable CA1303 // Don't police me on internationalization.

namespace Renouveau.ForPowerShell
{
  /// <summary>
  /// Shows that <c>SendToThreadPool</c> blocks reentrancy.
  /// </summary>
  [Cmdlet(VerbsLifecycle.Invoke, "Reenter")]
  public sealed class InvokeReenter : AsyncPSCmdlet
  {
    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
      base.BeginProcessing();
      SendToThreadPool(async dispatcher =>
      {
        try
        {
          await dispatcher.InvokeAsync(() =>
          {
            SendToThreadPool(async dispatcher => { });
          });
        }
        catch (InvalidOperationException ex)
        {
          await dispatcher.WriteObjectAsync(ex);
        }
      });
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
