<#

.NOTES
SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
SPDX-License-Identifier: MIT

This file is part of Renouveau Caffeine, available under the MIT license.
https://github.com/renouveau/caffeine

.SYNOPSIS
Tests whether the text files are in the correct format.

.DESCRIPTION
Tests whether the text files have UTF-8 BOM, embedded NUL (a symptom of UTF-16/32 encoding), or CR, and writes to the warning stream if so.  This script can be invoked from anywhere, but it will always work on the script file location.

#>
[CmdletBinding()]
Param()

Begin
{
  $CsHeader = [regex]::new('^// SPDX-FileCopyrightText: [^\n]+\n// SPDX-License-Identifier: [a-zA-Z0-9\.-]+\n');
  $TextExts = [System.Collections.Generic.HashSet[string]]::new();
  @('.cs', '.editorconfig', '.slnx', '.xml', '.csproj', '.md', '.gitignore', '.ps1', '.json', '.config', '.txt', '.runsettings', '.psd1', '.ps1xml', '.psm1') |
    ForEach-Object { $TextExts.Add($_.ToLowerInvariant()) } |
    Out-Null;
  $TextFiles = [System.Collections.Generic.HashSet[string]]::new();
  @('LICENSE', 'NOTICE') |
    ForEach-Object { $TextFiles.Add($_.ToLowerInvariant()) } |
    Out-Null;
  $NoCheckFiles = [System.Collections.Generic.Dictionary[string, string[]]]::new();
  # ignored/binary/special-format files
  $NoCheckExts = [System.Collections.Generic.HashSet[string]]::new();
  @('.exe', '.dll', '.obj', '.pdb', '.user', '.nupkg', '.png', '.gitmodules') |
    ForEach-Object { $NoCheckExts.Add($_.ToLowerInvariant()) } |
    Out-Null;
  $NoCheckDirs = [System.Collections.Generic.HashSet[string]]::new();
  @('.git', '.vs', 'bin', 'obj', 'Debug', 'Release', 'TestResults') |
    ForEach-Object { $NoCheckDirs.Add($_.ToLowerInvariant()) } |
    Out-Null;
  $LocalPathPrefix = '.' + [System.IO.Path]::DirectorySeparatorChar;
  $RootLength = $PSScriptRoot.Length;
  If (-not $PSScriptRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar))
  {
    $LocalPathPrefix = '.';
  }
  $CheckDir = {
    $local:items = @(Get-ChildItem "$($args[0])/*" -Force -File);
    ForEach ($local:item in $local:items)
    {
      $local:path = $LocalPathPrefix + $local:item.FullName.Substring($RootLength);
      If ($NoCheckFiles.ContainsKey($local:item.Name) -and
        $NoCheckFiles[$local:item.Name] -contains (Get-FileHash -LiteralPath ($local:item.FullName) -Algorithm SHA1).Hash)
      {
        # This file is skipped.
      }
      ElseIf ($TextFiles.Contains($local:item.Name.ToLowerInvariant()) -or
        $TextExts.Contains($local:item.Extension.ToLowerInvariant()))
      {
        $local:data = [System.IO.File]::ReadAllBytes($local:item.FullName);
        If ($local:data.Length -ge 3 -and
          $local:data[0] -eq 0xEF -and
          $local:data[1] -eq 0xBB -and
          $local:data[2] -eq 0xBF)
        {
          Write-Warning "Text file has BOM: $local:path";
        }
        If (13 -in $local:data)
        {
          Write-Warning "Text file has  CR: $local:path";
        }
        If (0 -in $local:data)
        {
          Write-Warning "Text file has NUL: $local:path";
        }
        If ($local:item.Extension.ToLowerInvariant() -ceq '.cs')
        {
          $local:data = [System.IO.File]::ReadAllText($local:item.FullName);
          If (-not $CsHeader.IsMatch($local:data))
          {
            Write-Warning ".cs lacks  header: $local:path";
          }
        }
      }
      ElseIf (-not $NoCheckExts.Contains($local:item.Extension.ToLowerInvariant()))
      {
        Write-Warning "Unknown file type: $local:path";
      }
    }
    $local:items = @(Get-ChildItem "$($args[0])/*" -Force -Directory);
    ForEach ($local:item in $local:items)
    {
      If (-not $NoCheckDirs.Contains($local:item.Name.ToLowerInvariant()))
      {
        $local:item = $local:item.FullName;
        & $CheckDir $local:item;
      }
    }
  };
  & $CheckDir $PSScriptRoot;
}
