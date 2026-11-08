// SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
// SPDX-License-Identifier: MIT

// This file is part of Renouveau AsyncPSCmdlet, available under the MIT license.
// https://github.com/renouveau/AsyncPSCmdlet

using System;
using System.Management.Automation;
using System.Reflection;
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

  #region PipelineStopToken, StopProcessingToken, ShouldStop, ThrowIfShouldStop, StopProcessing, StopProcessingExtras

  public abstract partial class AsyncPSCmdlet
  {
    private static readonly Func<Cmdlet, CancellationToken> get_PipelineStopToken_ = CreateGetPipelineStopTokenDelegate();

#pragma warning disable IDE0079 // This suppression is necessary for .NET Framework.
#pragma warning disable CA2263 // .NET Framework doesn't have generic `CreateDelegate`.
    private static Func<Cmdlet, CancellationToken> CreateGetPipelineStopTokenDelegate()
    {
      PropertyInfo? prop = typeof(Cmdlet).GetProperty(nameof(PipelineStopToken));
      if (prop is null)
        return new Func<Cmdlet, CancellationToken>(GetPipelineStopTokenShim);
      return (Func<Cmdlet, CancellationToken>)prop.GetMethod!.CreateDelegate(typeof(Func<Cmdlet, CancellationToken>));
    }
#pragma warning restore CA2263
#pragma warning restore IDE0079

    private static CancellationToken GetPipelineStopTokenShim(Cmdlet that) { return CancellationToken.None; }

    private CancellationTokenSource? spts_;

    private CancellationTokenSource StopProcessingTokenSource
    {
      get { return spts_ ?? GetSptsRare(); }
    }

    private CancellationTokenSource GetSptsRare()
    {
      CancellationTokenSource standby = new CancellationTokenSource();
      return Interlocked.CompareExchange(ref spts_, standby, null) ?? standby;
    }

    /// <summary>
    /// For PowerShell 7.6+, the same as <c><see cref="Cmdlet"/>.PipelineStopToken</c>;
    /// for lower versions, returns <c><see cref="CancellationToken.None"/></c>.
    /// This property can be retrieved from any thread.
    /// </summary>
    public /* new */ CancellationToken PipelineStopToken
    {
      get { return get_PipelineStopToken_(this); }
    }

    /// <summary>
    /// Gets the token that is canceled by <c><see cref="StopProcessing()"/></c>.
    /// This is <em>separate</em> from <c><see cref="PipelineStopToken"/></c>.
    /// This property can be retrieved from any thread.
    /// </summary>
    public CancellationToken StopProcessingToken
    {
      get { return StopProcessingTokenSource.Token; }
    }

    /// <summary>
    /// Gets whether processing should stop.
    /// This property can be retrieved from any thread.
    /// </summary>
    public bool ShouldStop
    {
      get
      {
        CancellationTokenSource? spts = spts_;
        return Stopping || get_PipelineStopToken_(this).IsCancellationRequested || ((spts is not null) && spts.IsCancellationRequested);
      }
    }

    /// <summary>
    /// Throws <c><see cref="PipelineStoppedException"/></c> if <c><see cref="ShouldStop"/></c> is <c><see langword="true"/></c>.
    /// This method can be called from any thread.
    /// </summary>
    protected void ThrowIfShouldStop()
    {
      if (get_PipelineStopToken_(this).IsCancellationRequested)
        ThrowPipelineStopped();
      CancellationTokenSource? spts = spts_;
      if (Stopping || ((spts is not null) && spts.IsCancellationRequested))
        ThrowStopProcessing();
    }

    private static void ThrowPipelineStopped()
    {
      throw new PipelineStoppedException();
    }

    private static void ThrowStopProcessing()
    {
      throw new PipelineStoppedException("The method 'Cmdlet.StopProcessing' has been called.");
    }

    /// <summary>
    /// <list type="number">
    /// <item>Cancel <c><see cref="StopProcessingToken"/></c>.</item>
    /// <item>Call <c><see cref="StopProcessingExtras()"/></c>.</item>
    /// <item>Call the <c><see langword="base"/></c> implementation, <c><see cref="Cmdlet.StopProcessing()"/></c>.</item>
    /// </list>
    /// </summary>
    protected sealed override void StopProcessing()
    {
      try
      {
        StopProcessingTokenSource.Cancel();
      }
      finally
      {
        try
        {
          StopProcessingExtras();
        }
        finally
        {
          base.StopProcessing();
        }
      }
    }

    /// <summary>
    /// This method is called by <c><see cref="StopProcessing()"/></c>.
    /// The default implementation does nothing.
    /// </summary>
    protected virtual void StopProcessingExtras() { }
  }

  #endregion PipelineStopToken, StopProcessingToken, ShouldStop, ThrowIfShouldStop, StopProcessing, StopProcessingExtras

}
