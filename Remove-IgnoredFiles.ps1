<#

.NOTES
SPDX-FileCopyrightText: Copyright 2026 Ji Luo, all rights reserved.
SPDX-License-Identifier: MIT

This file is part of Renouveau Caffeine, available under the MIT license.
https://github.com/renouveau/caffeine

.SYNOPSIS
Removes certain ignored files, then reports remaining untracked/ignored files (as reported by `git clean --dry-run -x -- <repo_root>`) in the verbose stream.

.DESCRIPTION
Removes the following.

```text
src/.vs
src/*/*.csproj.user
src/*/bin
src/*/obj
```

This script can be invoked from anywhere, but it will always work on the script file location.

#>
[CmdletBinding()]
Param()

Begin
{
  # In case the non-wildcard targets do not exist...
  @(
    'src/.vs',
    $null
  ) | ForEach-Object { If ($_ -ne $null) {
    New-Item ([System.IO.Path]::Combine($PSScriptRoot, $_)) -Type 'Directory' -Force -ErrorAction 'Ignore';
  } } | Out-Null;
}

End
{
  @(
    'src/.vs',
    'src/*/*.csproj.user',
    'src/*/bin',
    'src/*/obj',
    $null
  ) | ForEach-Object { If ($_ -ne $null) {
    Remove-Item ([System.IO.Path]::Combine($PSScriptRoot, $_)) -Force -Recurse;
  } };
  Write-Verbose 'Remove-IgnoredFiles.ps1: Done removing.';
  $local:GitCleanResult = git clean --dry-run -x -- "$PSScriptRoot" 2>&1;
  If ($LASTEXITCODE -ne 0)
  {
    Write-Error ("'git clean --dry-run -x -- <PSScriptRoot>' failed:`n" + ($GitCleanResult -join "`n"));
  }
  Else
  {
    Write-Verbose "'git clean --dry-run -x -- <PSScriptRoot>' output below";
    $GitCleanResult | ForEach-Object {
      If ($_ -ne $null)
      {
        $_ = $_.Trim();
        If ($_ -eq '')
        {
          # Do nothing.
        }
        ElseIf ($_.StartsWith('Would remove '))
        {
          Write-Verbose ('file: ' + $_.Substring(13));
        }
        Else
        {
          Write-Verbose ('(unparsed line) ' + $_);
        }
      }
    };
  }
}
