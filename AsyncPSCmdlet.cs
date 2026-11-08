// SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
// SPDX-License-Identifier: MIT

// This file is part of Renouveau AsyncPSCmdlet, available under the MIT license.
// https://github.com/renouveau/AsyncPSCmdlet

using System;
using System.Management.Automation;
using System.Threading;

namespace Renouveau.ForPowerShell
{
  /// <summary>
  /// Base class for cmdlets that call into asynchronous code.
  /// </summary>
  public abstract partial class AsyncPSCmdlet : PSCmdlet
  {
  }

  #region ManagedThreadIdentity

  public abstract partial class AsyncPSCmdlet
  {
    /// <summary>
    /// Initializes a new instance of <c><see cref="AsyncPSCmdlet"/></c>.
    /// </summary>
    protected AsyncPSCmdlet() { Affinity = ManagedThreadIdentity.Current; }

    private readonly ManagedThreadIdentity Affinity;

    /// <summary>
    /// I can't find documentation that prevents the following scenarios.
    /// <list type="bullet">
    /// <item>The same managed thread is represented by two different instances of <c><see cref="Thread"/></c>.</item>
    /// <item>
    /// A sequence of the following events.
    /// <list type="number">
    /// <item>An alive managed thread <c>t1</c> is represented by <c>obj</c> of type <c><see cref="Thread"/></c>.</item>
    /// <item>Another finalizable object <c>f</c> holds a reference to <c>obj</c>.</item>
    /// <item>The thread <c>t1</c> dies and both <c>f</c> and <c>obj</c> becomes eligible for finalization.</item>
    /// <item>The object <c>f</c> resurrects <c>obj</c>.</item>
    /// <item>The object <c>obj</c> is recycled to represent another managed thread.</item>
    /// </list>
    /// </item>
    /// </list>
    /// Therefore, we roll our own managed thread identity.
    /// </summary>
    private sealed class ManagedThreadIdentity
    {
      [ThreadStatic]
      private static ManagedThreadIdentity? current_;

      internal static ManagedThreadIdentity Current { get { return current_ ??= new ManagedThreadIdentity(); } }

      internal readonly int Numeric;

      private ManagedThreadIdentity() { Numeric = Environment.CurrentManagedThreadId; }
    }
  }

  #endregion ManagedThreadIdentity

}
