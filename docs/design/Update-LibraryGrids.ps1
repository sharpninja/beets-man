<#
.SYNOPSIS
Applies the shared Details grid to generated Library storyboard states.
.DESCRIPTION
Run after the storyboard export. The export's dynamic Track, Search and
Playlist states supply row order, selection, and cache status. The source
SVG supplies the columns and track rows used by those states.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$docs = Split-Path -Parent $PSScriptRoot
[xml]$shared = Get-Content -Raw -LiteralPath (Join-Path $docs 'wireframes/shared/library-grid.svg')
$header = $shared.SelectSingleNode("//*[@id='song-columns']/*")
$rows = @{}
foreach ($row in $shared.SelectNodes("//*[starts-with(@id,'song-') and @id!='song-columns']/*[@role='row' and not(@data-selected)]")) {
    $rows[$row.SelectNodes("*[@role='cell']")[1].InnerText] = $row
}
$playingGlyph = [string][char]0x25B6

function Format-SongGrid($Layer) {
    $children = @($Layer.ChildNodes | Where-Object NodeType -eq 'Element')
    $tracks = $Layer.SelectSingleNode("*[local-name()='text' and @x='611' and @y='263' and text()='Media']")
    $rowSize = 4
    $rowHeight = 43
    $top = 280
    $headerSize = 0
    if ($tracks) {
        $first = $Layer.SelectSingleNode("*[local-name()='rect' and @x='204' and @y='242' and @width='564' and @height='34']")
        $rowSize = 6
        $rowHeight = 46
        $top = 242
        $headerSize = 5
    } else {
        $first = $Layer.SelectSingleNode("*[local-name()='rect' and @x='204' and @y='280' and @width='564' and @height='43']")
    }
    if (-not $first) { return $false }
    $pending = $Layer.SelectSingleNode("*[local-name()='text' and @x='611' and text()='Pending']")
    $index = [array]::IndexOf($children, $first) + $headerSize
    $document = $Layer.OwnerDocument
    $grid = $document.CreateElement('g', $Layer.NamespaceURI)
    $grid.SetAttribute('data-library-grid', 'song')
    $grid.SetAttribute('role', 'table')
    $grid.SetAttribute('transform', "translate(0 $top)")
    [void]$grid.AppendChild($document.ImportNode($header, $true))
    $position = [int]$header.FirstChild.height
    $initialRow = $index
    while ($index -lt $children.Count) {
        $background = $children[$index]
        if ($background.LocalName -ne 'rect' -or $background.x -ne '204' -or $background.width -ne '564' -or $background.height -ne [string]$rowHeight) { break }
        $titleIndex = $index + 1
        if ($tracks) { $titleIndex++ }
        $title = $children[$titleIndex].InnerText
        if (-not $rows.ContainsKey($title)) { throw "Unknown library track: $title" }
        $row = $document.ImportNode($rows[$title], $true)
        $row.SetAttribute('transform', "translate(0 $position)")
        $rowBackground = $row.FirstChild
        if ($background.fill -eq '#e0ede4') {
            $rowBackground.SetAttribute('fill', '#e0ede4')
            $row.SetAttribute('data-selected', 'true')
        }
        $cell = $row.SelectNodes("*[@role='cell']")[1]
        if ($pending) {
            $status = $document.ImportNode($children[$index + 4], $true)
            $status.SetAttribute('x', $cell.x)
            $status.SetAttribute('y', '34')
            $status.SetAttribute('font-size', '10')
            $cell.SetAttribute('y', '17')
            [void]$row.InsertBefore($status, $cell)
        }
        if ($tracks -and $children[$index + 1].InnerText -eq $playingGlyph) {
            $playing = $document.ImportNode($children[$index + 1], $true)
            $playing.SetAttribute('x', $cell.x)
            $playing.SetAttribute('y', $cell.y)
            $playing.SetAttribute('aria-label', 'Playing')
            $playing.SetAttribute('font-size', '11')
            $cell.SetAttribute('x', [string]([int]$cell.x + 15))
            [void]$row.InsertBefore($playing, $cell)
        }
        [void]$grid.AppendChild($row)
        $position += [int]$rowBackground.height
        $index += $rowSize
    }
    if ($index -eq $initialRow) { return $false }
    [void]$Layer.InsertBefore($grid, $first)
    for ($i = $initialRow - $headerSize; $i -lt $index; $i++) {
        [void]$Layer.RemoveChild($children[$i])
    }
    $true
}

$originalCaption = 'Album art appears in the catalog beside the same queue.'
$gridCaption = 'Northern Lines is highlighted in the album grid beside the same queue.'
$syncDropFrames = '15-sync-list-open.svg', '17-sync-list-populated.svg', '18-sync-list-export-preview.svg'
$updated = 0
foreach ($asset in Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -Filter '*.svg') {
    $content = Get-Content -Raw -LiteralPath $asset.FullName
    $updatedContent = $content.Replace($originalCaption, $gridCaption)
    if ($asset.Directory.Name -eq 'STB10' -and $asset.Name -in $syncDropFrames) {
        $updatedContent = $updatedContent.Replace('Drag tracks or albums here', 'Drag tracks, albums or playlists here')
    }
    [xml]$svg = $updatedContent
    $changed = $updatedContent -cne $content
    foreach ($layer in $svg.SelectNodes("//*[local-name()='g']")) {
        if (Format-SongGrid $layer) { $changed = $true }
    }
    if ($changed -and $PSCmdlet.ShouldProcess($asset.FullName, 'Update Library storyboard state')) {
        $settings = [System.Xml.XmlWriterSettings]::new()
        $settings.OmitXmlDeclaration = $true
        $settings.Indent = $true
        $settings.IndentChars = '  '
        $buffer = [System.Text.StringBuilder]::new()
        $writer = [System.Xml.XmlWriter]::Create($buffer, $settings)
        try { $svg.Save($writer) } finally { $writer.Dispose() }
        Set-Content -LiteralPath $asset.FullName -Value $buffer.ToString() -Encoding utf8
        $updated++
    }
}
foreach ($relativePath in 'ui-asset-manifest.json', 'testing/ui/coverage.md') {
    $path = Join-Path $docs $relativePath
    $content = Get-Content -Raw -LiteralPath $path
    if ($content.Contains($originalCaption) -and $PSCmdlet.ShouldProcess($path, 'Sync the album selection caption')) {
        Set-Content -LiteralPath $path -Value $content.Replace($originalCaption, $gridCaption) -NoNewline -Encoding utf8
    }
}
"$updated review SVGs updated."
