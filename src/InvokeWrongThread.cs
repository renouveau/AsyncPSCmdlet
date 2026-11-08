// SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
// SPDX-License-Identifier: MIT

// This file is part of Renouveau AsyncPSCmdlet, available under the MIT license.
// https://github.com/renouveau/AsyncPSCmdlet

using System;
using System.Management.Automation;
using System.Threading;

#pragma warning disable CA2007 // No synchronization context is on the thread pool.
#pragma warning disable CA1303 // Don't police me on internationalization.

namespace Renouveau.ForPowerShell
{
  /// <summary>
  /// Shows that <c>SendToThreadPool</c> blocks invocation from wrong threads.
  /// </summary>
  [Cmdlet(VerbsLifecycle.Invoke, "WrongThread")]
  public sealed class InvokeWrongThread : AsyncPSCmdlet
  {
    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
      base.BeginProcessing();
      Exception? e = null;
      Thread th = new Thread(() =>
      {
        try
        {
          SendToThreadPool(async dispatcher => { });
        }
        catch (InvalidOperationException ex)
        {
          e = ex;
        }
      });
      th.Start();
      th.Join();
      WriteObject(e);
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
