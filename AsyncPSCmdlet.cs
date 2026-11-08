// SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
// SPDX-License-Identifier: MIT

// This file is part of Renouveau AsyncPSCmdlet, available under the MIT license.
// https://github.com/renouveau/AsyncPSCmdlet

#if NET461_OR_GREATER || NET7_0_OR_GREATER
#else
#error This file requires .NET Framework 4.6.1+ or .NET 7.0+.
#endif

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

  #region CallbackKind

  public abstract partial class AsyncPSCmdlet
  {
    private enum CallbackKind
    {
      Invalid,
      Action,
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
    }
  }

  #endregion CallbackKind

  #region Callback

  public abstract partial class AsyncPSCmdlet
  {
    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    private readonly struct Callback
    {
      internal Callback(CallbackKind kind, object? param1, object? param2, TaskCompletionSource<object?>? sink)
      {
        Kind = kind;
        Param1 = param1;
        Param2 = param2;
        Sink = sink;
      }

      internal readonly CallbackKind Kind;
      internal readonly object? Param1;
      internal readonly object? Param2;
      internal readonly TaskCompletionSource<object?>? Sink;

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
        TaskCompletionSource<object?>? sink = Sink;
        if (sink is null)
          ProcessPostBeforeStop(cmdlet, e);
        else
          ProcessSendBeforeStop(cmdlet, e, sink);
      }

      internal void ProcessAfterStop(Exception? e)
      {
        TaskCompletionSource<object?>? sink = Sink;
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
            ProcessInvalid();
            return;
          case CallbackKind.Action:
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

      private void ProcessSendBeforeStop(Cmdlet cmdlet, Exception? e, TaskCompletionSource<object?> sink)
      {
        switch (Kind)
        {
          default:
          case CallbackKind.Invalid:
            ProcessInvalid();
            return;
          case CallbackKind.Action:
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
          case CallbackKind.ThrowTerminatingError:
            ReceiveThrowTerminatingError(cmdlet, sink);
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
        }
      }

      private void ProcessInvalid()
      {
        throw new InvalidOperationException("Invalid value for 'Callback.Kind': " + Kind.ToString());
      }

#pragma warning disable CA1031 // Allow propagating arbitrary exceptions to the continuation.

      private void ReceiveAction(TaskCompletionSource<object?> sink)
      {
        try
        {
          ((Action)Param1!)();
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteObject1(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteObject(Param1!);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteObject2(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteObject(Param1!, Param2 is not null);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteError(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteError((ErrorRecord)Param1!);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveThrowTerminatingError(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.ThrowTerminatingError((ErrorRecord)Param1!);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
          // Propagate this terminating error.
          throw;
        }
      }

      private void ReceiveWriteWarning(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteWarning((string)Param1!);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteVerbose(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteVerbose((string)Param1!);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteDebug(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteDebug((string)Param1!);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteInformation1(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteInformation((InformationRecord)Param1!);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteInformation2(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteInformation(Param1!, (string[])Param2!);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteProgress(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteProgress((ProgressRecord)Param1!);
          sink.TrySetResult(null);
        }
        catch (Exception e)
        {
          sink.TrySetException(e);
        }
      }

      private void ReceiveWriteCommandDetail(Cmdlet cmdlet, TaskCompletionSource<object?> sink)
      {
        try
        {
          cmdlet.WriteCommandDetail((string)Param1!);
          sink.TrySetResult(null);
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
    /// </summary>
    [DebuggerDisplay(nameof(Name) + " = {" + nameof(Name) + "}")]
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
      /// </summary>
      public string Name { get; }

      private readonly AsyncPSCmdlet Owner;
      private readonly Queue<Callback> Callbacks;
      private readonly Func<TransientDispatcher, Task?> Work;

      /// <summary>
      /// Determines whether the work item has expired.
      /// </summary>
      public bool Expired { get; private set; }

      TransientDispatcher ITransientDispatcher.Create(string name, AsyncPSCmdlet owner, Queue<Callback> callbacks, Func<TransientDispatcher, Task?> work)
      {
        return new TransientDispatcher(name, owner, callbacks, work);
      }

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
    }
  }

  #endregion TransientDispatcher

  #region SendToThreadPool

  public abstract partial class AsyncPSCmdlet
  {
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
          dispatcher.Expired = true;
        }
      }
      // We're now outside the lock.
      task.Wait();
    }
#pragma warning restore CA1031

    /// <inheritdoc cref="SendToThreadPool(Func{TransientDispatcher, Task?}, string?)"/>
    protected void SendToThreadPool(Func<TransientDispatcher, Task?> work, [CallerFilePath] string path = "unknown_file", [CallerLineNumber] int line = -1, [CallerMemberName] string member = "unknown_member")
    {
      SendToThreadPool(work, path + ":" + line.ToString(CultureInfo.InvariantCulture) + " (" + member + ")");
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
