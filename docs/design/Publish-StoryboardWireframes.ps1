<#
.SYNOPSIS
Expands authored storyboard SVGs into self-contained Asset Review frames.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$sourceRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'wireframes'
$frameRoot = Join-Path $sourceRoot 'frames'
$documents = @{}

function Get-SvgDocument([string]$Path) {
    if (-not $documents.ContainsKey($Path)) {
        $documents[$Path] = [xml](Get-Content -Raw -LiteralPath $Path)
    }
    $documents[$Path]
}

function Expand-SvgReference($Parent, [string]$Source, [string[]]$Trail = @()) {
    foreach ($child in @($Parent.ChildNodes | Where-Object NodeType -eq 'Element')) {
        if ($child.LocalName -ne 'use') {
            Expand-SvgReference $child $Source $Trail
            continue
        }
        $file, $fragment = $child.GetAttribute('href').Split('#', 2)
        $path = $Source
        if ($file) { $path = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $Source) $file)) }
        $reference = "$path#$fragment"
        if (-not $fragment -or [IO.Path]::GetExtension($path) -ne '.svg' -or
            -not $path.StartsWith("$sourceRoot/") -or $reference -cin $Trail) {
            throw "Invalid or recursive SVG reference: $reference"
        }
        $target = (Get-SvgDocument $path).SelectNodes("//*[@id='$fragment']")
        if ($target.Count -ne 1) { throw "Expected one SVG component: $reference" }
        $instance = $Parent.OwnerDocument.ImportNode($target[0], $true)
        $instance.RemoveAttribute('id')
        Expand-SvgReference $instance $path ($Trail + $reference)
        [void]$Parent.ReplaceChild($instance, $child)
    }
}

$settings = [System.Xml.XmlWriterSettings]::new()
$settings.OmitXmlDeclaration = $true
$settings.Indent = $true
$settings.IndentChars = '  '
$published = 0
foreach ($frame in Get-ChildItem -LiteralPath $frameRoot -Recurse -Filter '*.svg') {
    $relative = [IO.Path]::GetRelativePath($sourceRoot, $frame.FullName)
    $output = Join-Path $PSScriptRoot $relative
    $document = (Get-SvgDocument $frame.FullName).CloneNode($true)
    Expand-SvgReference $document.DocumentElement $frame.FullName
    $buffer = [System.Text.StringBuilder]::new()
    $writer = [System.Xml.XmlWriter]::Create($buffer, $settings)
    try { $document.Save($writer) } finally { $writer.Dispose() }
    $svg = $buffer.ToString() + "`n"
    if ((Test-Path -LiteralPath $output) -and (Get-Content -Raw -LiteralPath $output) -ceq $svg) { continue }
    if ($PSCmdlet.ShouldProcess($output, 'Publish storyboard wireframe')) {
        Set-Content -LiteralPath $output -Value $svg -NoNewline -Encoding utf8
        $published++
    }
}
"$published authored storyboard frames published."
