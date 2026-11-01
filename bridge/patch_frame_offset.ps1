
$dir = "c:\work\agpt_plugin\src"
$f = join-path $dir "agpt_plugin_drawings.cs"
if (-not (test-path $f)) { write-host "file not found: $f"; exit 1 }
$c = [io.file]::readalltext($f)
$c2 = $c.replace("ContentBottom = 72.0", "ContentBottom = 90.0")
$old = @"
        public static void Bounds(Point3d origin, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = origin.X + DL.BindLeft + 4;
            y0 = origin.Y + DL.ContentBottom + 2;
            x1 = origin.X + DL.A3W - DL.Margin - 4;
            y1 = origin.Y + DL.ContentTop - 2;
        }

        /// <summary>true, если низ объекта (yBottom) не заходит в зону штампа.</summary>
        public static bool FitsAboveStamp(Point3d origin, double yBottom) =>
            yBottom >= origin.Y + DL.ContentBottom;
"@
$new = @"
        public static void Bounds(Point3d origin, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = origin.X + DL.BindLeft + 4;
            y0 = origin.Y + DL.ContentBottom + 6;
            x1 = origin.X + DL.A3W - DL.Margin - 4;
            y1 = origin.Y + DL.ContentTop - 2;
        }

        public static double FloorY(Point3d origin) =>
            origin.Y + DL.ContentBottom + 6;

        public static bool FitsAboveStamp(Point3d origin, double yBottom) =>
            yBottom >= FloorY(origin);
"@
if ($c2.contains("FloorY")) { write-host "FloorY already present" }
elseif ($c2.contains($old)) { $c2 = $c2.replace($old, $new); write-host "SheetArea updated" }
else { write-host "SheetArea block not matched - only ContentBottom may change" }
[io.file]::writealltext($f, $c2)
write-host "done"
select-string -path $f -pattern "ContentBottom" | foreach-object { $_.line }
select-string -path $f -pattern "FloorY" | foreach-object { $_.line }
