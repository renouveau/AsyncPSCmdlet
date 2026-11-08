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
            throw new ArgumentException("1");
          });
        }
        catch (ArgumentException ex)
        {
          Console.WriteLine("Writing exception object.");
          await dispatcher.WriteObjectAsync(ex);
          Console.WriteLine("Done writing exception object.");
        }
        dispatcher.InvokeAndForget(() => throw new ArgumentException("2"));
        Console.WriteLine("After InvokeAndForget throw 2");
        await Task.Delay(500);
        try
        {
          await dispatcher.InvokeAsync(() => throw new ArgumentException("3"));
        }
        catch (InvalidOperationException ex)
        {
          Console.WriteLine("InvalidOperaionException: " + ex);
        }
        catch (ArgumentException ex)
        {
          Console.WriteLine("ArgumentException: " + ex);
        }
      });
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
