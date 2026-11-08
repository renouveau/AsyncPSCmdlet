// SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
// SPDX-License-Identifier: MIT

// This file is part of Renouveau AsyncPSCmdlet, available under the MIT license.
// https://github.com/renouveau/AsyncPSCmdlet

using System;
using System.Management.Automation;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CA2007 // No synchronization context is on the thread pool.
#pragma warning disable CA1303 // Don't police me on internationalization.

namespace Renouveau.ForPowerShell
{
  /// <summary>
  /// Exemplifies
  /// <c><see cref="AsyncPSCmdlet.TransientDispatcher.InvokeAndForget(Action)"/></c>
  /// and
  /// <c><see cref="AsyncPSCmdlet.TransientDispatcher.InvokeAsync(Action)"/></c>.
  /// </summary>
  [Cmdlet(VerbsLifecycle.Invoke, "Invoke")]
  public sealed class InvokeInvoke : AsyncPSCmdlet
  {
    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
      base.BeginProcessing();
      DateTime start = DateTime.Now;
      Console.WriteLine("Before SendToThreadPool: " + start);
      SendToThreadPool(async dispatcher =>
      {
        dispatcher.InvokeAndForget(() => WriteObject((DateTime.Now - start).TotalSeconds + "s"));
        Task task1 = Task.Run(async () =>
        {
          await Task.Delay(5000);
          await dispatcher.InvokeAsync(() => WriteObject("task1: " + (DateTime.Now - start).TotalSeconds + "s"));
        });
        Task task2 = Task.Run(async () =>
        {
          await Task.Delay(5000);
          await dispatcher.InvokeAsync(() => WriteObject("task2: " + (DateTime.Now - start).TotalSeconds + "s"));
        });
        await task2;
        await task1;
        dispatcher.InvokeAndForget(() => Thread.Sleep(5000));
        Console.WriteLine("ThreadPool delegate is about to return: " + (DateTime.Now - start).TotalSeconds + "s");
      });
      Console.WriteLine("After SendToThreadPool: " + (DateTime.Now - start).TotalSeconds + "s");
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
