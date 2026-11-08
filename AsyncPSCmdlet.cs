// SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
// SPDX-License-Identifier: MIT

// This file is part of Renouveau AsyncPSCmdlet, available under the MIT license.
// https://github.com/renouveau/AsyncPSCmdlet

#if NET461_OR_GREATER || NET7_0_OR_GREATER
#else
#error This file requires .NET Framework 4.6.1+ or .NET 7.0+.
#endif

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Renouveau.ForPowerShell
{
  /// <summary>
  /// Base class for cmdlets that call into asynchronous code.
  /// This class is modified from <a href="https://github.com/renouveau/AsyncPSCmdlet">Renouveau AsyncPSCmdlet</a>
  /// version <a href="https://github.com/renouveau/AsyncPSCmdlet/tree/v0.2.0"><c>0.2.0</c></a>.
  /// <br /><br />
  /// If you start from a non-tagged commit,
  /// use
  /// <code>
  /// commit <a href="https://github.com/renouveau/AsyncPSCmdlet/tree/commit-hash"><c>commit-hash</c></a>.
  /// </code>
  /// instead.
  /// </summary>
  public abstract partial class AsyncPSCmdlet : PSCmdlet
  {
    /// <summary>
    /// Initializes a new instance of <c><see cref="AsyncPSCmdlet"/></c>.
    /// </summary>
    protected AsyncPSCmdlet()
    {
      SyncRoot = new object();
      Affinity = ManagedThreadIdentity.Current;
      spts_ = null;
      CurrentWorkItem = null;
      ReusableCallbackQueue = null;
    }

    private readonly object SyncRoot;

    private readonly struct UnlockGuard : IDisposable
    {
      private readonly object Target;

      internal UnlockGuard(object target)
      {
        Target = target;
        Monitor.Exit(target);
      }

      public void Dispose()
      {
        Exception? ex = null;
        bool taken = false;
      Retry:
        // Obtaining the lock is very important because
        // this type is used inside a `lock` statement.
        try
        {
          Monitor.Enter(Target, ref taken);
        }
#pragma warning disable CA1031 // We handle crazy exceptions.
        catch (Exception e)
#pragma warning restore CA1031
        {
          ex = e;
          if (!taken)
            goto Retry;
        }
        if (ex is not null)
          throw ex;
      }
    }
  }

  #region ManagedThreadIdentity

  public abstract partial class AsyncPSCmdlet
  {
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
    [DebuggerDisplay("managed thread {" + nameof(Numeric) + "}")]
    private sealed class ManagedThreadIdentity
    {
      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      [ThreadStatic]
      private static ManagedThreadIdentity? current_;

      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      internal static ManagedThreadIdentity Current { get { return current_ ??= new ManagedThreadIdentity(); } }

      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      internal readonly int Numeric;

      private ManagedThreadIdentity() { Numeric = Environment.CurrentManagedThreadId; }
    }
  }

  #endregion ManagedThreadIdentity

  #region PipelineStopToken, StopProcessingToken, ShouldStop, ThrowIfShouldStop, StopProcessing, StopProcessingExtras

  public abstract partial class AsyncPSCmdlet
  {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
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

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private CancellationTokenSource? spts_;

    /// <summary>
    /// It's very difficult to dispose of this object correctly.
    /// In its current implementation,
    /// an undisposed-of token source leaks at most one <c><see cref="ManualResetEvent"/></c>,
    /// so we let the finalizer of <c><see cref="ManualResetEvent"/></c> take care of that.
    /// </summary>
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
      finally
      {
        object sr = SyncRoot;
        lock (sr)
          Monitor.PulseAll(sr);
      }
    }

    /// <summary>
    /// This method is called by <c><see cref="StopProcessing()"/></c> and must be prepared to be called from any thread.
    /// The default implementation does nothing.
    /// </summary>
    protected virtual void StopProcessingExtras() { }
  }

  #endregion PipelineStopToken, StopProcessingToken, ShouldStop, ThrowIfShouldStop, StopProcessing, StopProcessingExtras

  #region CallbackKind

  public abstract partial class AsyncPSCmdlet
  {
    private enum CallbackKind
    {
      Invalid,
      Invoke,
      WriteObject1,
      WriteObject2,
      WriteError,
      ThrowTerminatingError,
      WriteWarning,
      WriteVerbose,
      WriteDebug,
      WriteInformation1,
      WriteInformation2,
      WriteProgress,
      WriteCommandDetail,
      ShouldContinue2,
      ShouldContinue4,
      ShouldContinue5,
      ShouldProcess1,
      ShouldProcess2,
      ShouldProcess3,
      ShouldProcess4,
    }
  }

  #endregion CallbackKind

  #region parameter packs

  public abstract partial class AsyncPSCmdlet
  {
    private sealed class ShouldContinueProcessPack
    {
      public ShouldContinueProcessPack(string? param1, string? param2, string? param3)
      {
        Param1 = param1;
        Param2 = param2;
        Param3 = param3;
      }

      internal readonly string? Param1;
      internal readonly string? Param2;
      internal readonly string? Param3;
    }

#pragma warning disable CA1051 // The consumer should have by-reference access to the boxed instances.
    /// <summary>
    /// Provides a boxed site for by-reference parameters to <c>ShouldContinue</c> and <c>ShouldProcess</c>.
    /// </summary>
    protected sealed class ShouldContinueProcessBox
    {
      /// <summary>
      /// Represents the <c>shouldProcessReason</c> parameter of <c><see cref="Cmdlet.ShouldProcess(string, string, string, out ShouldProcessReason)"/></c>.
      /// </summary>
      public ShouldProcessReason ShouldProcessReason;

      /// <summary>
      /// Represents the <c>yesToAll</c> parameter of
      /// <c><see cref="Cmdlet.ShouldContinue(string, string, ref bool, ref bool)"/></c>
      /// and
      /// <c><see cref="Cmdlet.ShouldContinue(string, string, bool, ref bool, ref bool)"/></c>.
      /// </summary>
      public bool YesToAll;

      /// <summary>
      /// Represents the <c>noToAll</c> parameter of
      /// <c><see cref="Cmdlet.ShouldContinue(string, string, ref bool, ref bool)"/></c>
      /// and
      /// <c><see cref="Cmdlet.ShouldContinue(string, string, bool, ref bool, ref bool)"/></c>.
      /// </summary>
      public bool NoToAll;
    }
#pragma warning restore CA1051
  }

  #endregion parameter packs

  #region Callback

  public abstract partial class AsyncPSCmdlet
  {
    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private readonly struct Callback
    {
      internal Callback(CallbackKind kind, object? param1, object? param2, TaskCompletionSource<bool>? sink)
      {
        Kind = kind;
        Param1 = param1;
        Param2 = param2;
        Sink = sink;
      }

      internal readonly CallbackKind Kind;
      internal readonly object? Param1;
      internal readonly object? Param2;
      internal readonly TaskCompletionSource<bool>? Sink;

      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      private string DebuggerDisplay
      {
        get
        {
          return (Sink is null ? "fire-and-forget: " : "await: ") + Kind.ToString();
        }
      }

      internal void ProcessBeforeStop(Cmdlet cmdlet, Exception? e)
      {
        TaskCompletionSource<bool>? sink = Sink;
        if (sink is null)
          ProcessPostBeforeStop(cmdlet, e);
        else
          ProcessSendBeforeStop(cmdlet, e, sink);
      }

      internal void ProcessAfterStop(Exception? e)
      {
        TaskCompletionSource<bool>? sink = Sink;
        if (sink is null)
          return;
        if (e is null)
          sink.TrySetCanceled();
        else
          sink.TrySetException(e);
      }

      private void ProcessPostBeforeStop(Cmdlet cmdlet, Exception? e)
      {
        switch (Kind)
        {
          default:
          case CallbackKind.Invalid:
            ProcessPostInvalid();
            return;
          case CallbackKind.Invoke:
            if (e is null)
              ((Action)Param1!)();
            // Skip any fire-and-forget delegates
            // when a previous fire-and-forget callback faulted
            // or something extreme happened.
            return;
          case CallbackKind.WriteObject1:
            cmdlet.WriteObject(Param1!);
            return;
          case CallbackKind.WriteObject2:
            cmdlet.WriteObject(Param1!, Param2 is not null);
            return;
          case CallbackKind.WriteError:
            cmdlet.WriteError((ErrorRecord)Param1!);
            return;
          case CallbackKind.ThrowTerminatingError:
            cmdlet.ThrowTerminatingError((ErrorRecord)Param1!);
            return;
          case CallbackKind.WriteWarning:
            cmdlet.WriteWarning((string)Param1!);
            return;
          case CallbackKind.WriteVerbose:
            cmdlet.WriteVerbose((string)Param1!);
            return;
          case CallbackKind.WriteDebug:
            cmdlet.WriteDebug((string)Param1!);
            return;
          case CallbackKind.WriteInformation1:
            cmdlet.WriteInformation((InformationRecord)Param1!);
            return;
          case CallbackKind.WriteInformation2:
            cmdlet.WriteInformation(Param1!, (string[])Param2!);
            return;
          case CallbackKind.WriteProgress:
            cmdlet.WriteProgress((ProgressRecord)Param1!);
            return;
          case CallbackKind.WriteCommandDetail:
            cmdlet.WriteCommandDetail((string)Param1!);
            return;
        }
      }

      private void ProcessSendBeforeStop(Cmdlet cmdlet, Exception? e, TaskCompletionSource<bool> sink)
      {
        switch (Kind)
        {
          default:
          case CallbackKind.Invalid:
          case CallbackKind.ThrowTerminatingError:
            ProcessSendInvalid();
            return;
          case CallbackKind.Invoke:
            if (e is null)
              ReceiveAction(sink);
            else
              sink.TrySetException(e);
            // Do not attempt awaited delegates
            // when a previous fire-and-forget callback faulted
            // or something extreme happened.
            return;
          case CallbackKind.WriteObject1:
            ReceiveWriteObject1(cmdlet, sink);
            return;
          case CallbackKind.WriteObject2:
            ReceiveWriteObject2(cmdlet, sink);
            return;
          case CallbackKind.WriteError:
            ReceiveWriteError(cmdlet, sink);
            return;
          case CallbackKind.WriteWarning:
            ReceiveWriteWarning(cmdlet, sink);
            return;
          case CallbackKind.WriteVerbose:
            ReceiveWriteVerbose(cmdlet, sink);
            return;
          case CallbackKind.WriteDebug:
            ReceiveWriteDebug(cmdlet, sink);
            return;
          case CallbackKind.WriteInformation1:
            ReceiveWriteInformation1(cmdlet, sink);
            return;
          case CallbackKind.WriteInformation2:
            ReceiveWriteInformation2(cmdlet, sink);
            return;
          case CallbackKind.WriteProgress:
            ReceiveWriteProgress(cmdlet, sink);
            return;
          case CallbackKind.WriteCommandDetail:
            ReceiveWriteCommandDetail(cmdlet, sink);
            return;
          case CallbackKind.ShouldContinue2:
            ReceiveShouldContinue2(cmdlet, sink);
            return;
          case CallbackKind.ShouldContinue4:
            ReceiveShouldContinue4(cmdlet, sink);
            return;
          case CallbackKind.ShouldContinue5:
            ReceiveShouldContinue5(cmdlet, sink);
            return;
          case CallbackKind.ShouldProcess1:
            ReceiveShouldProcess1(cmdlet, sink);
            return;
          case CallbackKind.ShouldProcess2:
            ReceiveShouldProcess2(cmdlet, sink);
            return;
          case CallbackKind.ShouldProcess3:
            ReceiveShouldProcess3(cmdlet, sink);
            return;
          case CallbackKind.ShouldProcess4:
            ReceiveShouldProcess4(cmdlet, sink);
            return;
        }
      }

      private void ProcessPostInvalid()
      {
        throw new InvalidOperationException("Invalid value for 'Callback.Kind' in fire-and-forget: " + Kind.ToString());
      }

      private void ProcessSendInvalid()
      {
        throw new InvalidOperationException("Invalid value for 'Callback.Kind' in awaited tasks: " + Kind.ToString());
      }

#pragma warning disable CA1031 // Allow propagating arbitrary exceptions to the continuation.

      private void ReceiveAction(TaskCompletionSource<bool> sink)
      {
        try
        {
          ((Action)Param1!)();
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteObject1(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteObject(Param1!);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteObject2(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteObject(Param1!, Param2 is not null);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteError(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteError((ErrorRecord)Param1!);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteWarning(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteWarning((string)Param1!);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteVerbose(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteVerbose((string)Param1!);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteDebug(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteDebug((string)Param1!);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteInformation1(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteInformation((InformationRecord)Param1!);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteInformation2(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteInformation(Param1!, (string[])Param2!);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteProgress(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteProgress((ProgressRecord)Param1!);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteCommandDetail(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          cmdlet.WriteCommandDetail((string)Param1!);
          sink.TrySetResult(false);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveShouldContinue2(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          sink.TrySetResult(cmdlet.ShouldContinue((string)Param1!, (string)Param2!));
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveShouldContinue4(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          ShouldContinueProcessPack pack = (ShouldContinueProcessPack)Param1!;
          ShouldContinueProcessBox box = (ShouldContinueProcessBox)Param2!;
          sink.TrySetResult(cmdlet.ShouldContinue(pack.Param1, pack.Param2, ref box.YesToAll, ref box.NoToAll));
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveShouldContinue5(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          ShouldContinueProcessPack pack = (ShouldContinueProcessPack)Param1!;
          ShouldContinueProcessBox box = (ShouldContinueProcessBox)Param2!;
          sink.TrySetResult(cmdlet.ShouldContinue(pack.Param1, pack.Param2, pack.Param3 is not null, ref box.YesToAll, ref box.NoToAll));
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveShouldProcess1(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          sink.TrySetResult(cmdlet.ShouldProcess((string)Param1!));
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveShouldProcess2(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          sink.TrySetResult(cmdlet.ShouldProcess((string)Param1!, (string)Param2!));
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveShouldProcess3(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          ShouldContinueProcessPack pack = (ShouldContinueProcessPack)Param1!;
          sink.TrySetResult(cmdlet.ShouldProcess(pack.Param1, pack.Param2, pack.Param3));
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveShouldProcess4(Cmdlet cmdlet, TaskCompletionSource<bool> sink)
      {
        try
        {
          ShouldContinueProcessPack pack = (ShouldContinueProcessPack)Param1!;
          ShouldContinueProcessBox box = (ShouldContinueProcessBox)Param2!;
          sink.TrySetResult(cmdlet.ShouldProcess(pack.Param1, pack.Param2, pack.Param3, out box.ShouldProcessReason));
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

#pragma warning restore CA1031

    }
  }

  #endregion Callback

  #region TransientDispatcher

  public abstract partial class AsyncPSCmdlet
  {
    /// <summary>
    /// Makes accessibility effectively <c><see langword="private"/></c> in <c><see cref="AsyncPSCmdlet"/></c>.
    /// </summary>
    private interface ITransientDispatcher
    {
      string Name { get; }
      TransientDispatcher Create(string name, AsyncPSCmdlet owner, Queue<Callback> callbacks, Func<TransientDispatcher, Task?> work);
      bool Expired { get; set; }
      Task? ThreadPoolWorker();
    }

    /// <summary>
    /// Represents a work item submitted to the thread pool by <c><see cref="AsyncPSCmdlet"/></c>
    /// and
    /// provides the cmdlet services from any thread for the duration of the work item.
    /// <list type="bullet">
    /// <item>
    /// Entry to public methods is validated eagerly and exceptions are thrown synchronously.
    /// This is true even for <c>XxxAsync</c>.
    /// Calls are queued synchronously and linearly, implying a strong ordering.
    /// A call returns once the request is queued.
    /// </item>
    /// <item>
    /// Public methods <c>XxxAndForget</c> are fire-and-forget.
    /// Exceptions thrown by the delegate or during <c>WriteXxx</c> cause the work item to expire.
    /// </item>
    /// <item>
    /// Public methods <c>XxxAsync</c> produce (awaitable) tasks.
    /// The task completes when the request is performed by the <c><see cref="AsyncPSCmdlet"/></c> on its thread.
    /// Exceptions thrown by the delegate or during <c>WriteXxx</c> are reported back to the task.
    /// </item>
    /// <item>
    /// A work item expires
    /// when the pipeline is stopped,
    /// when <c><see cref="Cmdlet.StopProcessing()"/></c> is called,
    /// when an exception escapes fire-and-forget methods on the thread of <c><see cref="AsyncPSCmdlet"/></c>,
    /// when the work completes,
    /// whichever is the earliest.
    /// When a work item expires, further calls to communications methods fail synchronously.
    /// </item>
    /// <item>
    /// When a work item expires, all pending communication calls are accelerated.
    /// <list type="bullet">
    /// <item>PowerShell stream writes are attempted until stopping.</item>
    /// <item>Delegates are not attempted.</item>
    /// <item>A best-effort notification of failure is made to all outstanding tasks.</item>
    /// </list>
    /// </item>
    /// </list>
    /// Communication methods on this object can<em>not</em> be called from the thread of the owning <c><see cref="AsyncPSCmdlet"/></c>, by design.
    /// <list type="bullet">
    /// <item>
    /// If the call happens directly in a delegate posted/sent by
    /// <c><see cref="InvokeAndForget(Action)"/></c> or <c><see cref="InvokeAsync(Action)"/></c>,
    /// use the methods on <c><see cref="Cmdlet"/></c>.
    /// </item>
    /// <item>
    /// If you set up a nested message loop in a delegate,
    /// the call cannot preserve queueing semantics without risking deadlock.
    /// </item>
    /// </list>
    /// </summary>
    [DebuggerDisplay(nameof(Expired) + " = {" + nameof(Expired) + "}, " + nameof(Name) + " = {" + nameof(Name) + ", nq}")]
    protected sealed class TransientDispatcher : ITransientDispatcher
    {
      private TransientDispatcher(string name, AsyncPSCmdlet owner, Queue<Callback> callbacks, Func<TransientDispatcher, Task?> work)
      {
        Name = name;
        Owner = owner;
        Callbacks = callbacks;
        Work = work;
        Expired = false;
      }

      /// <summary>
      /// Gets a description of the work item.
      /// This property can be retrieved from any thread.
      /// </summary>
      public string Name { get; }

      private readonly AsyncPSCmdlet Owner;
      private readonly Queue<Callback> Callbacks;
      private readonly Func<TransientDispatcher, Task?> Work;

      /// <summary>
      /// Determines whether the work item has expired.
      /// This property can be retrieved from any thread.
      /// </summary>
      public bool Expired { get; private set; }

      #region explicit implementations of ITransientDispatcher members

      TransientDispatcher ITransientDispatcher.Create(string name, AsyncPSCmdlet owner, Queue<Callback> callbacks, Func<TransientDispatcher, Task?> work)
      {
        return new TransientDispatcher(name, owner, callbacks, work);
      }

      [DebuggerBrowsable(DebuggerBrowsableState.Never)]
      bool ITransientDispatcher.Expired { get { return Expired; } set { Expired = value; } }

      async Task? ITransientDispatcher.ThreadPoolWorker()
      {
        try
        {
          await (Work(this) ?? Task.CompletedTask).ConfigureAwait(false);
        }
        finally
        {
          object sr = Owner.SyncRoot;
          lock (sr)
          {
            Expired = true;
            Monitor.PulseAll(sr);
          }
        }
      }

      #endregion explicit implementations of ITransientDispatcher members

      private void ThrowRepulsion()
      {
        throw new InvalidOperationException("Communication methods cannot be called on thread "
          + Owner.Affinity.Numeric.ToString(CultureInfo.InvariantCulture)
          + ", on which the instance was constructed. "
          + "(The numeric identifier might be stale if the thread has exited.)");
      }

      private void ThrowExpired()
      {
        throw new InvalidOperationException("The work item '" + Name + "' has expired.");
      }

      private static void ThrowAction()
      {
        throw new ArgumentNullException("action");
      }

      private static void ThrowErrorRecord()
      {
        throw new ArgumentNullException("errorRecord");
      }

      private static void ThrowText()
      {
        throw new ArgumentNullException("text");
      }

      private static void ThrowInformationRecord()
      {
        throw new ArgumentNullException("informationRecord");
      }

      private static void ThrowProgressRecord()
      {
        throw new ArgumentNullException("progressRecord");
      }

      private static void ThrowToAll()
      {
        throw new ArgumentNullException("toAll");
      }

      private static void ThrowShouldProcessReason()
      {
        throw new ArgumentNullException("shouldProcessReason");
      }

      #region fire-and-forget

      /// <summary>
      /// Posts a delegate to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public void InvokeAndForget(Action action)
      {
        if (action is null)
          ThrowAction();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.Invoke, action, null, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteObject(object)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public void WriteObjectAndForget(object? postToPipeline)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteObject1, postToPipeline, null, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteObject(object, bool)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public void WriteObjectAndForget(object? postToPipeline, bool enumerateCollection)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteObject2, postToPipeline, enumerateCollection ? this : null, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteError(ErrorRecord)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public void WriteErrorAndForget(ErrorRecord errorRecord)
      {
        if (errorRecord is null)
          ThrowErrorRecord();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteError, errorRecord, null, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.ThrowTerminatingError(ErrorRecord)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>
      /// and
      /// throws <c><see cref="PipelineStoppedException"/></c>.
      /// </summary>
      /// <exception cref="PipelineStoppedException"/>
      public void ThrowTerminatingErrorAndForget(ErrorRecord errorRecord)
      {
        if (errorRecord is null)
          ThrowErrorRecord();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.ThrowTerminatingError, errorRecord, null, null));
        }
        throw new PipelineStoppedException("The method 'ThrowTerminatingErrorAndForget' has been called.");
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteWarning(string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="text"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public void WriteWarningAndForget(string text)
      {
        if (text is null)
          ThrowText();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteWarning, text, null, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteVerbose(string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="text"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public void WriteVerboseAndForget(string text)
      {
        if (text is null)
          ThrowText();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteVerbose, text, null, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteDebug(string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="text"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public void WriteDebugAndForget(string text)
      {
        if (text is null)
          ThrowText();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteDebug, text, null, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteInformation(InformationRecord)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="informationRecord"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public void WriteInformationAndForget(InformationRecord informationRecord)
      {
        if (informationRecord is null)
          ThrowInformationRecord();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteInformation1, informationRecord, null, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteInformation(object, string[])"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public void WriteInformationAndForget(object? messageData, string[]? tags)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteInformation2, messageData, tags, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteProgress(ProgressRecord)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="progressRecord"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public void WriteProgressAndForget(ProgressRecord progressRecord)
      {
        if (progressRecord is null)
          ThrowProgressRecord();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteProgress, progressRecord, null, null));
        }
      }

      /// <summary>
      /// Posts a request to call <c><see cref="Cmdlet.WriteCommandDetail(string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="text"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public void WriteCommandDetailAndForget(string text)
      {
        if (text is null)
          ThrowText();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteCommandDetail, text, null, null));
        }
      }

      #endregion fire-and-forget

      #region awaited tasks

      /// <summary>
      /// Sends a delegate to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public Task InvokeAsync(Action action)
      {
        if (action is null)
          ThrowAction();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.Invoke, action, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteObject(object)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public Task WriteObjectAsync(object? sendToPipeline)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteObject1, sendToPipeline, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteObject(object, bool)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public Task WriteObjectAsync(object? sendToPipeline, bool enumerateCollection)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteObject2, sendToPipeline, enumerateCollection ? this : null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteError(ErrorRecord)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public Task WriteErrorAsync(ErrorRecord errorRecord)
      {
        if (errorRecord is null)
          ThrowErrorRecord();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteError, errorRecord, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// <strong>Posts</strong> a call of <c><see cref="Cmdlet.ThrowTerminatingError(ErrorRecord)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>
      /// and
      /// throws <c><see cref="PipelineStoppedException"/></c> <strong>synchronously</strong>.
      /// </summary>
      /// <exception cref="PipelineStoppedException"/>
      public Task ThrowTerminatingErrorAsync(ErrorRecord errorRecord)
      {
        if (errorRecord is null)
          ThrowErrorRecord();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.ThrowTerminatingError, errorRecord, null, null));
        }
        throw new PipelineStoppedException("The method 'ThrowTerminatingErrorAsync' has been called.");
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteWarning(string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="text"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public Task WriteWarningAsync(string text)
      {
        if (text is null)
          ThrowText();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteWarning, text, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteVerbose(string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="text"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public Task WriteVerboseAsync(string text)
      {
        if (text is null)
          ThrowText();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteVerbose, text, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteDebug(string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="text"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public Task WriteDebugAsync(string text)
      {
        if (text is null)
          ThrowText();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteDebug, text, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteInformation(InformationRecord)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="informationRecord"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public Task WriteInformationAsync(InformationRecord informationRecord)
      {
        if (informationRecord is null)
          ThrowInformationRecord();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteInformation1, informationRecord, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteInformation(object, string[])"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public Task WriteInformationAsync(object? messageData, string[]? tags)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteInformation2, messageData, tags, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteProgress(ProgressRecord)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="progressRecord"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public Task WriteProgressAsync(ProgressRecord progressRecord)
      {
        if (progressRecord is null)
          ThrowProgressRecord();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteProgress, progressRecord, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.WriteCommandDetail(string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="text"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public Task WriteCommandDetailAsync(string text)
      {
        if (text is null)
          ThrowText();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.WriteCommandDetail, text, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.ShouldContinue(string, string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public Task<bool> ShouldContinueAsync(string? query, string? caption)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.ShouldContinue2, query, caption, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.ShouldContinue(string, string, ref bool, ref bool)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="toAll"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public Task<bool> ShouldContinueAsync(string? query, string? caption, ShouldContinueProcessBox toAll)
      {
        if (toAll is null)
          ThrowToAll();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          ShouldContinueProcessPack pack = new ShouldContinueProcessPack(query, caption, null);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.ShouldContinue4, pack, toAll, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.ShouldContinue(string, string, bool, ref bool, ref bool)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="toAll"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public Task<bool> ShouldContinueAsync(string? query, string? caption, bool hasSecurityImpact, ShouldContinueProcessBox toAll)
      {
        if (toAll is null)
          ThrowToAll();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          ShouldContinueProcessPack pack = new ShouldContinueProcessPack(query, caption, hasSecurityImpact ? "hasSecurityImpact = true" : null);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.ShouldContinue5, pack, toAll, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.ShouldProcess(string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public Task<bool> ShouldProcessAsync(string? target)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.ShouldProcess1, target, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.ShouldProcess(string, string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public Task<bool> ShouldProcessAsync(string? target, string? action)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.ShouldProcess2, target, action, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.ShouldProcess(string, string, string)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// </summary>
      public Task<bool> ShouldProcessAsync(string? verboseDescription, string? verboseWarning, string? caption)
      {
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          ShouldContinueProcessPack pack = new ShouldContinueProcessPack(verboseDescription, verboseWarning, caption);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.ShouldProcess3, pack, null, source));
          return source.Task;
        }
      }

      /// <summary>
      /// Sends a call of <c><see cref="Cmdlet.ShouldProcess(string, string, string, out ShouldProcessReason)"/></c> to the <c><see cref="AsyncPSCmdlet"/></c>.
      /// This method does not allow <c><paramref name="shouldProcessReason"/></c> to be <c><see langword="null"/></c>.
      /// </summary>
      public Task<bool> ShouldProcessAsync(string? verboseDescription, string? verboseWarning, string? caption, ShouldContinueProcessBox shouldProcessReason)
      {
        if (shouldProcessReason is null)
          ThrowShouldProcessReason();
        AsyncPSCmdlet owner = Owner;
        if (owner.Affinity == ManagedThreadIdentity.Current)
          ThrowRepulsion();
        object sr = owner.SyncRoot;
        lock (sr)
        {
          if (Expired || owner.CurrentWorkItem != this)
            ThrowExpired();
          TaskCompletionSource<bool> source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
          ShouldContinueProcessPack pack = new ShouldContinueProcessPack(verboseDescription, verboseWarning, caption);
          Monitor.PulseAll(sr);
          Callbacks.Enqueue(new Callback(CallbackKind.ShouldProcess4, pack, shouldProcessReason, source));
          return source.Task;
        }
      }

      #endregion awaited tasks

    }
  }

  #endregion TransientDispatcher

  #region SendToThreadPool

  public abstract partial class AsyncPSCmdlet
  {
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private static readonly ITransientDispatcher TransientDispatcherFactory = (ITransientDispatcher)FormatterServices.GetUninitializedObject(typeof(TransientDispatcher));

    private ITransientDispatcher? CurrentWorkItem;
    private Queue<Callback>? ReusableCallbackQueue;

    /// <summary>
    /// Sends a work item to the thread pool and waits for it to complete,
    /// while allowing the work item to reenter
    /// via the <c><see cref="TransientDispatcher"/></c> instance.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// When <c><paramref name="work"/></c> is <c><see langword="null"/></c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// When the current thread is not the one on which <c><see langword="this"/></c> was constructed,
    /// or
    /// when a work item is in progress (this method cannot be reentered).
    /// </exception>
    /// <exception cref="PipelineStoppedException">
    /// When <c><see cref="ShouldStop"/></c> is <c><see langword="true"/></c>.
    /// </exception>
#pragma warning disable CA1031 // Allow swallowing exceptions.
    protected void SendToThreadPool(Func<TransientDispatcher, Task?> work, string? name)
    {
      if (work is null)
        ThrowWorkNull();
      if (Affinity != ManagedThreadIdentity.Current)
        ThrowAffinity();
      if (CurrentWorkItem is not null)
        ThrowReentrancy();
      ThrowIfShouldStop();
      name ??= "unknown";
      object sr = SyncRoot;
      Queue<Callback> queue = ReusableCallbackQueue ?? new Queue<Callback>();
      ITransientDispatcher dispatcher = TransientDispatcherFactory.Create(name, this, queue, work!);
      Func<Task?> worker = dispatcher.ThreadPoolWorker;
      Task? task = null;
      lock (sr)
      {
        try
        {
          ReusableCallbackQueue = null;
          CurrentWorkItem = dispatcher;
          task = Task.Run(worker);
          while (!dispatcher.Expired)
          {
            Monitor.Wait(sr);
            ThrowIfShouldStop();
            DrainQueueBeforeStop(queue, this, null, sr);
          }
          Debug.Assert(queue.Count == 0, "The callback queue must be empty if no exceptions occurred on the main thread.");
          ReusableCallbackQueue = queue;
        }
        catch (PipelineStoppedException)
        {
          dispatcher.Expired = true;
          try
          {
            DrainQueueAfterStop(queue, null, sr);
          }
          catch (Exception e)
          {
            DrainQueueAfterStop(queue, e, sr);
          }
          throw;
        }
        catch (Exception e)
        {
          dispatcher.Expired = true;
          // The likely cause is that a fire-and-forget callback faulted.
          // It could also arise from extreme conditions such as out of memory.
          try
          {
            DrainQueueBeforeStop(queue, this, e, sr);
          }
          catch (PipelineStoppedException)
          {
            DrainQueueAfterStop(queue, e, sr);
          }
          throw;
        }
        finally
        {
          CurrentWorkItem = null;
        }
      }
      // We're now outside the lock.
      task.Wait();
    }
#pragma warning restore CA1031

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private static readonly char[] PathSeps_ = { '\\', '/' };

    private static string? GetAutoWorkItemName(string? path, int line, string? member)
    {
      if (string.IsNullOrEmpty(path))
        path = "UnknownFile";
      if (string.IsNullOrEmpty(member))
        member = "UnknownMember";
      int index = path!.LastIndexOfAny(PathSeps_);
      string file = path;
      if (0 <= index && index < path.Length - 1)
        file = path.Substring(index + 1);
      string strline = line.ToString(CultureInfo.InvariantCulture);
      return member + " @ " + file + ":" + strline + " @ " + path + ":" + strline;
    }

    /// <inheritdoc cref="SendToThreadPool(Func{TransientDispatcher, Task?}, string?)"/>
    protected void SendToThreadPool(Func<TransientDispatcher, Task?> work, [CallerFilePath] string? path = null, [CallerLineNumber] int line = -1, [CallerMemberName] string? member = null)
    {
      SendToThreadPool(work, GetAutoWorkItemName(path, line, member));
    }

    private static void ThrowWorkNull()
    {
      throw new ArgumentNullException("work");
    }

    private void ThrowAffinity()
    {
      throw new InvalidOperationException("The method 'AsyncPSCmdlet.SendToThreadPool' is called on thread "
        + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)
        + ", but it can only be called on thread "
        + Affinity.Numeric.ToString(CultureInfo.InvariantCulture)
        + ", on which the instance was constructed. "
        + "(The numeric identifier(s) might be stale if the thread(s) has/have exited.)");
    }

    private void ThrowReentrancy()
    {
      throw new InvalidOperationException("The method 'AsyncPSCmdlet.SendToThreadPool' cannot be reentered. "
        + "The current work item in progress is '"
        + CurrentWorkItem!.Name
        + "'.");
    }

    private static void DrainQueueBeforeStop(Queue<Callback> queue, Cmdlet cmdlet, Exception? e, object sr)
    {
      while (queue.Count != 0)
      {
        Callback cb = queue.Dequeue();
        using (new UnlockGuard(sr))
          cb.ProcessBeforeStop(cmdlet, e);
      }
    }

    private static void DrainQueueAfterStop(Queue<Callback> queue, Exception? e, object sr)
    {
      while (queue.Count != 0)
      {
        Callback cb = queue.Dequeue();
        using (new UnlockGuard(sr))
          cb.ProcessAfterStop(e);
      }
    }
  }

  #endregion SendToThreadPool

}
