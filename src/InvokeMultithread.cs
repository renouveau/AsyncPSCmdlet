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
  /// Exemplifies
  /// <c><see cref="AsyncPSCmdlet.TransientDispatcher.InvokeAndForget(Action)"/></c>
  /// and
  /// <c><see cref="AsyncPSCmdlet.TransientDispatcher.InvokeAsync(Action)"/></c>.
  /// </summary>
  [Cmdlet(VerbsLifecycle.Invoke, "Multithread")]
  public sealed class InvokeMultithread : AsyncPSCmdlet
  {
    /// <summary>
    /// A number related to the task size.
    /// </summary>
    [Parameter(Mandatory = true, Position = 0)]
    public int Count { get; set; }

    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
      base.BeginProcessing();
      SendToThreadPool(dispatcher =>
      {
        Task[] tasks = new Task[Count];
        for (int i = 0; i < tasks.Length; ++i)
        {
          tasks[i] = Task.Run(() =>
          {
            Task[] subtasks = new Task[Count];
            for (int j = 0; j < subtasks.Length; ++j)
              subtasks[j] = dispatcher.WriteObjectAsync(new string('*', j + 1));
            return Task.WhenAll(subtasks);
          });
        }
        return Task.WhenAll(tasks);
      });
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
