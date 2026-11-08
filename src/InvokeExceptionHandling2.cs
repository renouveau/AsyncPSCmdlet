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
  /// Exemplifies exception handling.
  /// </summary>
  [Cmdlet(VerbsLifecycle.Invoke, "ExceptionHandling2")]
  public sealed class InvokeExceptionHandling2 : AsyncPSCmdlet
  {
    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
      base.BeginProcessing();
      SendToThreadPool(async dispatcher =>
      {
        // This exception propagates out of `SendToThreadPool`.
        throw new ArgumentException("Thrown from thread pool delegate.");
      });
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
