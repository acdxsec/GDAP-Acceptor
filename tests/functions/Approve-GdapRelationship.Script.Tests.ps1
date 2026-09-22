Describe 'Approve-GdapRelationship safety regressions' {
    It 'confirms approval without prompting for browser setup or cleanup' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.WhatIf.Checks.ps1'
        foreach ($policy in @('Explicit', 'Default')) {
            $output = & pwsh -NoProfile -NonInteractive -File $checks -RequireApprovalConfirmation -ConfirmationPolicy $policy 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
        }
    }
    It 'accepts the observed portal layout without changing its terms and rejects consent drift' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.PortalContract.Checks.ps1'
        $output = & pwsh -NoProfile -File $checks 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
        $output[-1] | Should -BeLike 'PASS:*'
    }
    It 'reports the observed portal layout without permitting an unverified approval mapping' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.PortalShape.Checks.ps1'
        $output = & pwsh -NoProfile -File $checks 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
        $output[-1] | Should -BeLike 'PASS:*'
    }
    It 'preserves the session User-Agent through real portal bootstrap and validation requests' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.PortalBootstrap.Checks.ps1'
        foreach ($cookie in @('Missing', 'Present')) {
            $output = & pwsh -NoProfile -File $checks -TenantCookie $cookie 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
            $output[-1] | Should -BeLike 'PASS:*'
        }
    }
    It 'passes a valid User-Agent through the real login bootstrap helpers' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.LoginBootstrap.Checks.ps1'
        $output = & pwsh -NoProfile -File $checks 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
        $output[-1] | Should -BeLike 'PASS:*'
    }
    It 'navigates before inspection and stops on unsafe or failed invitation navigation' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.WhatIf.Checks.ps1'
        foreach ($scenario in @('Ready', 'WrongRoute', 'NavigationFailure', 'BrowserClosed', 'TenantChanged', 'MissingId')) {
            $output = & pwsh -NoProfile -File $checks -InvitationScenario $scenario 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
        }
        $output = & pwsh -NoProfile -File $checks -SubmitSyntheticApproval 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
    }
    It 'waits for portal sign-in instead of exchanging an early ESTS cookie' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.BrowserCompletion.Checks.ps1'
        foreach ($scenario in @('Complete', 'Close', 'Timeout', 'ValidationFailure')) {
            $output = & pwsh -NoProfile -File $checks -Scenario $scenario 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
        }
    }
    It 'uses live identity independently of tenant cookie format or presence' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.WhatIf.Checks.ps1'
        foreach ($format in @('Quoted', 'Opaque', 'Missing')) {
            $output = & pwsh -NoProfile -File $checks -TenantCookieFormat $format 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
        }
    }
    It 'rejects live tenant drift and unreadable identity without approving' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.WhatIf.Checks.ps1'
        foreach ($scenario in @('Wrong', 'WrongExpected', 'Missing', 'Conflict', 'HttpError', 'TransportError', 'ShellFallback')) {
            $output = & pwsh -NoProfile -File $checks -TenantCookieFormat Missing -LiveTenantScenario $scenario 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
        }
    }
    It 'allows private browser prerequisites under WhatIf without submitting approval' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.WhatIf.Checks.ps1'
        foreach ($cookiePath in @('/', '/adminportal')) {
            $output = & pwsh -NoProfile -File $checks -TenantCookiePath $cookiePath 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
            $output[-1] | Should -BeLike 'PASS:*'
        }
    }
    It 'loads portal parameter types in a fresh process before browser authentication' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.Startup.Checks.ps1'
        $output = & pwsh -NoProfile -File $checks 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
    }
    It 'passes the isolated approval contract checks without authenticating' {
        $checks = Join-Path $PSScriptRoot 'Approve-GdapRelationship.Checks.ps1'
        $output = & pwsh -NoProfile -File $checks 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($output -join [Environment]::NewLine)
        $output[-1] | Should -BeLike 'PASS:*'
    }
}
