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
  /// Exemplifies <c>Write...</c> methods.
  /// </summary>
  [Cmdlet(VerbsLifecycle.Invoke, "Write")]
  public sealed class InvokeWrite : AsyncPSCmdlet
  {
    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
      base.BeginProcessing();
      SendToThreadPool(async dispatcher =>
      {
        // Verify that `WriteObject` accepts `null`.
        await Task.Delay(500);
        dispatcher.WriteObjectAndForget(null);
        dispatcher.WriteObjectAndForget(null, false);
        dispatcher.WriteObjectAndForget(null, true);
        await Task.Delay(500);
        await dispatcher.WriteObjectAsync(null);
        await dispatcher.WriteObjectAsync(null, false);
        await dispatcher.WriteObjectAsync(null, true);
        await Task.Delay(500);
        DateTime[] dt = [DateTime.Now, DateTime.UtcNow];
        dispatcher.WriteObjectAndForget(dt);
        dispatcher.WriteObjectAndForget(dt, false);
        dispatcher.WriteObjectAndForget(dt, true);
        await Task.Delay(500);
        dispatcher.WriteObjectAndForget(dt);
        dispatcher.WriteObjectAndForget(dt, false);
        dispatcher.WriteObjectAndForget(dt, true);
      });
    }

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
      SendToThreadPool(async dispatcher =>
      {
        // WriteError
        dispatcher.WriteErrorAndForget(new ErrorRecord(new ArgumentException("WriteError1"), "TestError1", ErrorCategory.InvalidOperation, this));
        await dispatcher.WriteErrorAsync(new ErrorRecord(new ArgumentException("WriteError2"), "TestError2", ErrorCategory.InvalidOperation, this));
        try
        {
          dispatcher.WriteErrorAndForget(null!);
        }
        catch (ArgumentNullException) { }
        try
        {
          await dispatcher.WriteErrorAsync(null!);
        }
        catch (ArgumentNullException) { }
        // WriteWarning
        dispatcher.WriteWarningAndForget("WriteWarningAndForget");
        await dispatcher.WriteWarningAsync("WriteWarningAsync");
        try
        {
          dispatcher.WriteWarningAndForget(null!);
        }
        catch (ArgumentNullException) { }
        try
        {
          await dispatcher.WriteWarningAsync(null!);
        }
        catch (ArgumentNullException) { }
        // WriteVerbose
        dispatcher.WriteVerboseAndForget("WriteVerboseAndForget");
        await dispatcher.WriteVerboseAsync("WriteVerboseAsync");
        try
        {
          dispatcher.WriteVerboseAndForget(null!);
        }
        catch (ArgumentNullException) { }
        try
        {
          await dispatcher.WriteVerboseAsync(null!);
        }
        catch (ArgumentNullException) { }
        // WriteDebug
        dispatcher.WriteDebugAndForget("WriteDebugAndForget");
        await dispatcher.WriteDebugAsync("WriteDebugAsync");
        try
        {
          dispatcher.WriteDebugAndForget(null!);
        }
        catch (ArgumentNullException) { }
        try
        {
          await dispatcher.WriteDebugAsync(null!);
        }
        catch (ArgumentNullException) { }
        // WriteInformation
        dispatcher.WriteInformationAndForget(new InformationRecord("info1", "source1"));
        await dispatcher.WriteInformationAsync(new InformationRecord("info2", "source2"));
        try
        {
          dispatcher.WriteInformationAndForget(null!);
        }
        catch (ArgumentNullException) { }
        try
        {
          await dispatcher.WriteInformationAsync(null!);
        }
        catch (ArgumentNullException) { }
        dispatcher.WriteInformationAndForget("info3", ["tag3"]);
        await dispatcher.WriteInformationAsync("info4", ["tag4"]);
        dispatcher.WriteInformationAndForget(null, null);
        await dispatcher.WriteInformationAsync(null, null);
        // WriteProgress
        dispatcher.WriteProgressAndForget(new ProgressRecord(123, "prog123", "good") { PercentComplete = 50, RecordType = ProgressRecordType.Processing });
        await Task.Delay(1000);
        await dispatcher.WriteProgressAsync(new ProgressRecord(123, "prog123", "good") { PercentComplete = 100, RecordType = ProgressRecordType.Completed });
        try
        {
          dispatcher.WriteProgressAndForget(null!);
        }
        catch (ArgumentNullException) { }
        try
        {
          await dispatcher.WriteProgressAsync(null!);
        }
        catch (ArgumentNullException) { }
        // WriteCommandDetail
        dispatcher.WriteCommandDetailAndForget("WriteCommandDetailAndForget");
        await dispatcher.WriteCommandDetailAsync("WriteCommandDetailAsync");
        try
        {
          dispatcher.WriteCommandDetailAndForget(null!);
        }
        catch (ArgumentNullException) { }
        try
        {
          await dispatcher.WriteCommandDetailAsync(null!);
        }
        catch (ArgumentNullException) { }
        // ThrowTerminatingError
        try
        {
          dispatcher.ThrowTerminatingErrorAndForget(new ErrorRecord(new InvalidOperationException("ThrowTerminatingErrorAndForget"), "ThrowTerminatingErrorAndForget-Id", ErrorCategory.ConnectionError, this));
        }
        catch (PipelineStoppedException pse)
        {
          dispatcher.WriteObjectAndForget("Write 1 after ThrowTerminatingErrorAndForget");
          _ = dispatcher.WriteObjectAsync("Write 2 after ThrowTerminatingErrorAndForget");
          Console.WriteLine(pse);
        }
        await Task.Delay(1000);
        try
        {
          await dispatcher.WriteObjectAsync("Write long after ThrowTerminatingErrorAndForget");
        }
        catch (Exception what)
        {
          Console.WriteLine(what);
          throw;
        }
      });
      base.EndProcessing();
    }
  }
}

#pragma warning restore CA1303
#pragma warning restore CA2007
