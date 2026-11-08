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
  /// Exemplifies exception handling.
  /// </summary>
  [Cmdlet(VerbsLifecycle.Invoke, "ExceptionHandling")]
  public sealed class InvokeExceptionHandling : AsyncPSCmdlet
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
            // This exception is propagated out of `await`.
            throw new ArgumentException("1");
          });
        }
        catch (ArgumentException ex)
        {
          // And it is caught here.
          Console.WriteLine("Writing exception object.");
          await dispatcher.WriteObjectAsync(ex);
          Console.WriteLine("Done writing exception object.");
        }
        // This exception causes the work item to terminate early.
        // This is bad usage, so avoid it!
        dispatcher.InvokeAndForget(() => throw new ArgumentException("2"));
        Console.WriteLine("After InvokeAndForget throw 2");
        // Nevertheless, the thread pool delegate can continue.
        await Task.Delay(500);
        try
        {
          // This call fails synchronously because the work item has expired.
          await dispatcher.InvokeAsync(() => throw new ArgumentException("3"));
        }
        catch (InvalidOperationException ex)
        {
          Console.WriteLine("InvalidOperaionException: " + ex);
        }
        catch (ArgumentException ex)
        {
          // This `catch` should not be reached.
          Console.WriteLine("ArgumentException: " + ex);
        }
      });
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
