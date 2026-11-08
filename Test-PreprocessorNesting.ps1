<#

.NOTES
SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
SPDX-License-Identifier: MIT

This file is part of Renouveau Neat, available under the MIT license.
https://github.com/renouveau/neat

.SYNOPSIS
Tests whether C# source code files have correctly nested #pragma warning disable/restore and #if/elif/else/endif.

.DESCRIPTION
The policy is to disable/restore exactly one warning with each #pragma directive, to restore warnings in a stack manner, and to nest warning manipulations and conditionals.  This script checks for violations of this policy.  This script can be invoked from anywhere, but it will always work on the script file location.

#>
[CmdletBinding()]
Param()

Begin
{
  $script:Files = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File -Recurse -Force;
  $script:regexPragma = [regex]'#pragma';
  $script:regexBranch = [regex]'#(if|elif|else|endif)';
  $script:regexPreprocessLine = [regex]::new('^#pragma warning (disable|restore) ([a-zA-Z0-9]+)( +//[^\n]*)?$|^#(if|elif|else|endif)($| )', 'Multiline');
  $script:stack = [System.Collections.Generic.Stack[string]]::new();
  $script:reported = $false;
  ForEach ($script:File in $script:Files)
  {
    $script:File = $script:File.FullName;
    If ($script:File.Replace([System.IO.Path]::DirectorySeparatorChar, '/').IndexOf('/.git/', [System.StringComparison]::Ordinal) -ge 0)
    {
      Continue;
    }
    $script:Text = [System.IO.File]::ReadAllText($script:File, [System.Text.Encoding]::UTF8);
    $script:PragmaCount = $script:regexPragma.Matches($script:Text).Count;
    $script:BranchCount = $script:regexBranch.Matches($script:Text).Count;
    If ($script:PragmaCount -eq 0 -and $script:BranchCount -eq 0)
    {
      Continue;
    }
    $script:Matches = $script:regexPreprocessLine.Matches($script:Text) | Write-Output;
    If ($script:Matches.Count -ne $script:PragmaCount + $script:BranchCount)
    {
      Write-Warning "Test-PreprocessorNesting.ps1: Found unrecognized #(pragma|if|elif|else|endif).`n$script:File";
      Continue;
    }
    $script:stack.Clear();
    $script:reported = $false;
    ForEach ($script:Match in $script:Matches)
    {
      If ($script:Match.Groups[1].Value -eq 'disable')
      {
        $script:stack.Push($script:Match.Groups[2].Value);
      }
      ElseIf ($script:Match.Groups[1].Value -eq 'restore')
      {
        If ($script:stack.Count -eq 0)
        {
          Write-Warning "Test-PreprocessorNesting.ps1: Dangling #pragma warning restore.`n$script:File`n$($script:Match.Value)";
          $script:reported = $true;
          Break;
        }
        If ($script:stack.Peek() -ne $script:Match.Groups[2].Value)
        {
          Write-Warning "Test-PreprocessorNesting.ps1: #(pragma warning|if|elif|else|endif) are not properly stacked.`n$script:File`n$($script:Match.Value)";
          $script:reported = $true;
          Break;
        }
        $null = $script:stack.Pop();
      }
      ElseIf ($script:Match.Groups[4].Value -eq 'if')
      {
        $script:stack.Push('#if');
      }
      Else # elif, else, endif
      {
        If ($script:stack.Count -eq 0)
        {
          Write-Warning "Test-PreprocessorNesting.ps1: Dangling #(elif|else|endif).`n$script:File`n$($script:Match.Value)";
          $script:reported = $true;
          Break;
        }
        If (-not $script:stack.Peek().StartsWith('#if'))
        {
          Write-Warning "Test-PreprocessorNesting.ps1: #(pragma warning|if|elif|else|endif) are not properly stacked.`n$script:File`n$($script:Match.Value)";
          $script:reported = $true;
          Break;
        }
        If ($script:Match.Groups[4].Value -eq 'endif')
        {
          $null = $script:stack.Pop();
        }
        ElseIf ($script:stack.Peek().EndsWith('-else'))
        {
          # ^^^ This ending can only be #if, -elif, or -else.
          Write-Warning "Test-PreprocessorNesting.ps1: #(elif|else) after #else.`n$script:File`n$($script:Match.Value)";
          $script:reported = $true;
          Break;
        }
        Else
        {
          $script:stack.Push($script:stack.Pop() + '-' + $script:Match.Groups[4].Value);
        }
      }
    }
    If (-not $script:reported -and $script:stack.Count -ne 0)
    {
      Write-Warning "Test-PreprocessorNesting.ps1: Stack is not empty at EOF.`n$script:File";
      $script:reported = $true;
    }
    If ($script:reported)
    {
      Write-Debug 'Test-PreprocessorNesting.ps1: Here is a chance to watch the script variables.';
    }
    Else
    {
      Write-Verbose "Test-PreprocessorNesting.ps1: $script:File";
    }
  }
  Write-Verbose 'Test-PreprocessorNesting.ps1: Finished checking.';
}
