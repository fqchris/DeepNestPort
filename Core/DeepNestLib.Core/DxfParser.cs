using IxMilia.Dxf.Entities;
using netDxf;
using netDxf.Entities;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;

namespace DeepNestLib
{
    public class DxfParser
    {
        /// <summary>
        /// netDxf's release build swallows every reader exception and returns NULL from
        /// DxfDocument.Load — the old bare Load call then NRE'd on the first dereference,
        /// discarding the whole part with a bare "Object reference not set" (#548).
        /// Known live class: Inventor flat-pattern DXFs carrying owner-handle references
        /// (group code 330) to objects the export never wrote; netDxf NREs resolving them
        /// in DxfReader.PostProcesses. Owner links are irrelevant to nesting geometry, so
        /// on a null load strip every 330 code/value pair (a DXF is strictly alternating
        /// code/value lines) and retry from memory. Still null → throw with a message that
        /// names the file and the repair already tried, never a bare NRE.
        /// </summary>
        private static netDxf.DxfDocument LoadDocument(string path)
        {
            var doc = netDxf.DxfDocument.Load(path);
            if (doc != null) return doc;

            string[] lines = File.ReadAllLines(path);
            var kept = new List<string>(lines.Length);
            for (int i = 0; i + 1 < lines.Length; i += 2)
            {
                if (lines[i].Trim() == "330") continue;
                kept.Add(lines[i]);
                kept.Add(lines[i + 1]);
            }
            if (lines.Length % 2 == 1) kept.Add(lines[lines.Length - 1]);

            using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", kept))))
            {
                doc = netDxf.DxfDocument.Load(ms);
            }
            if (doc == null)
                throw new InvalidDataException(
                    $"netDxf could not load '{Path.GetFileName(path)}' (returned null even after stripping owner-handle group 330; " +
                    "netDxf swallows the real reader exception in release builds)");
            Console.Error.WriteLine($"[DxfParser] Repaired dangling owner handles (group 330) to load {Path.GetFileName(path)}");
            return doc;
        }

        /// <summary>
        /// Parse a DXF into chained contours, in MILLIMETRES.
        ///
        /// <para><paramref name="mmPerUnitOverride"/> is the caller's full millimetres-per-drawing-unit
        /// verdict for this file. It exists because <see cref="RemoveThreshold"/> and
        /// <see cref="ClosingThreshold"/> are ABSOLUTE MILLIMETRE constants applied AFTER
        /// <c>mult</c>: left to its own devices this parser converts INCHES and nothing else, so a
        /// file declaring anything other than inches or millimetres reached the thresholds in its own
        /// drawing units — <c>ClosingThreshold = 0.1</c> meant 0.1 inch (2.54 mm) on an undeclared
        /// inch file and 0.1 foot (30.5 mm) on a Feet file, chaining together entities that are
        /// nowhere near each other. Pass the full factor (25.4 for inches, 1.0 for mm, 10.0 for cm,
        /// …) and every threshold below is back in the millimetres it was written for.</para>
        ///
        /// <para>Omit it (or pass a non-positive value) and the historical inches-only rule applies,
        /// which is what the DeepNestPort UI and console sample still do.</para>
        /// </summary>
        public static RawDetail[] LoadDxf(string path, bool split = false, double mmPerUnitOverride = 0)
        {
            FileInfo fi = new FileInfo(path);

            RawDetail s = new RawDetail();

            s.Name = fi.FullName;


            List<DraftElement> elems = new List<DraftElement>();


            netDxf.DxfDocument doc = LoadDocument(path);
            double mult = 1;
            if (mmPerUnitOverride > 0)
            {
                mult = mmPerUnitOverride;
            }
            else if (doc.DrawingVariables.InsUnits == netDxf.Units.DrawingUnits.Inches)
            {
                mult = 25.4;
            }
            if (mult != 1)
                Console.Error.WriteLine($"[DxfParser] Unit conversion: {doc.DrawingVariables.InsUnits} -> mm (mult={mult}) for {Path.GetFileName(path)}");

            foreach (var polyline2D in doc.Entities.Polylines2D)
            {
                var cc = new LocalContour();
                var list = polyline2D.PolygonalVertexes(100);

                cc.Points.AddRange(list.Select(z => new PointF((float)z.X, (float)z.Y)));
                var p = new PolylineElement
                {
                    Tag = new PolylineExportInfo()
                    {
                        IsClosed = polyline2D.IsClosed,
                        Points = list.Select(z => new Vector3(z.X * mult, z.Y * mult, 0)).ToArray()
                    },
                    Start = cc.Points.First(),
                    End = cc.Points.Last(),
                    Points = cc.Points.ToArray()
                };
                elems.Add(p);
            }
            foreach (var cr in doc.Entities.Circles)
            {

                LocalContour cc = new LocalContour();

                for (int i = 0; i <= 360; i += 15)
                {
                    var ang = i * Math.PI / 180f;
                    var xx = cr.Center.X + cr.Radius * Math.Cos(ang);
                    var yy = cr.Center.Y + cr.Radius * Math.Sin(ang);
                    cc.Points.Add(new PointF((float)xx, (float)yy));
                }
                PolylineElement p = new PolylineElement() { Tag = cr };
                elems.Add(p);
                p.Start = cc.Points[0];
                p.End = cc.Points[cc.Points.Count - 1];
                p.Points = cc.Points.ToArray();
            }
            foreach (var cr in doc.Entities.Arcs)
            {
                var sang = cr.StartAngle;
                var eang = cr.EndAngle;
                var center = new PointF((float)cr.Center.X, (float)cr.Center.Y);
                List<PointF> pp = new List<PointF>();

                if (sang > eang)
                {
                    sang -= 360;
                }

                for (double i = sang; i < eang; i += 15)
                {
                    var tt = GetPointFromAngle(center, (float)cr.Radius, i);
                    pp.Add(new PointF((float)tt.X, (float)tt.Y));
                }
                var t = GetPointFromAngle(center, (float)cr.Radius, eang);
                pp.Add(new PointF((float)t.X, (float)t.Y));
                PolylineElement p = new PolylineElement() { Tag = cr };
                elems.Add(p);

                p.Start = pp[0];
                p.End = pp[pp.Count - 1];
                p.Points = pp.ToArray();
            }
            foreach (var cr in doc.Entities.Splines)
            {
                LocalContour cc = new LocalContour();
                // #548 fallback-audit: one untessellatable spline must not discard the whole
                // part — skip just this entity, loudly, and let the rest of the outline survive.
                IList<Vector3> list;
                try
                {
                    list = cr.PolygonalVertexes(100);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DxfParser] Skipping untessellatable spline in {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }
                if (list == null || list.Count == 0) continue;

                cc.Points.AddRange(list.Select(z => new PointF((float)(float)z.X, (float)z.Y)));
                PolylineElement p = new PolylineElement()
                {
                    Tag = new PolylineExportInfo()
                    {
                        IsClosed = cr.IsClosed,
                        Points = list.Select(z => new Vector3(z.X * mult, z.Y * mult, 0)).ToArray()
                    }
                };
                elems.Add(p);
                p.Start = cc.Points[0];
                p.End = cc.Points[cc.Points.Count - 1];
                p.Points = cc.Points.ToArray();
            }
            foreach (var item in doc.Entities.Lines)
            {
                elems.Add(new LineElement()
                {
                    Tag = item,
                    Start = new PointF((float)item.StartPoint.X, (float)item.StartPoint.Y),
                    End = new PointF((float)item.EndPoint.X, (float)item.EndPoint.Y)
                });
            }

            List<RawDetail> ret = new List<RawDetail>();

            foreach (var item in elems)
            {
                item.Mult(mult);
            }
            elems = elems.Where(z => z.Length > RemoveThreshold).ToList();
            var cntrs2 = ConnectElements(elems.ToArray());
            if (split)
            {
                var nfps = cntrs2;
                for (int i = 0; i < nfps.Length; i++)
                {
                    for (int j = 0; j < nfps.Length; j++)
                    {
                        if (i != j)
                        {
                            var d2 = nfps[i];
                            var d3 = nfps[j];
                            var f0 = d3.Points[0];

                            if (GeometryUtil.pnpoly(d2.Points.ToArray(), f0.X, f0.Y))
                            {
                                d3.Parent = d2;
                                if (!d2.Childrens.Contains(d3))
                                {
                                    d2.Childrens.Add(d3);
                                }
                            }
                        }
                    }
                }

                var tops = nfps.Where(z => z.Parent == null).ToArray();
                for (int i = 0; i < tops.Length; i++)
                {
                    RawDetail det = new RawDetail()
                    {
                        Name = fi.FullName + "_" + i
                    };
                    if (tops[i].Points.Count < 3)
                        continue;

                    det.Outers.Add(tops[i]);
                    ret.Add(det);
                }
            }
            else
            {
                // Filter out contours with fewer than 3 points (invalid polygons)
                // This can happen when LINE entities can't be connected due to precision gaps
                var validContours = cntrs2.Where(z => z.Points.Count >= 3).ToArray();
                if (validContours.Length == 0)
                {
                    throw new Exception("few points - no valid contours found");
                }
                s.Outers.AddRange(validContours);

                ret.Add(s);
            }
            return ret.ToArray();
        }

        /// <summary>Shortest element (MILLIMETRES) that is real geometry rather than a duplicate point.</summary>
        public static double RemoveThreshold = 10e-5;

        /// <summary>
        /// Largest end-to-end gap (MILLIMETRES) that still counts as the same contour. Both this and
        /// <see cref="RemoveThreshold"/> are applied to coordinates already multiplied by
        /// <c>mult</c>, so they only mean millimetres when the caller passed a full
        /// <c>mmPerUnitOverride</c> to <see cref="LoadDxf(string, bool, double)"/>.
        /// </summary>
        public static double ClosingThreshold = 0.1;

        public static LocalContour[] ConnectElements(DraftElement[] elems)
        {
            List<LocalContour> ret = new List<LocalContour>();

            List<PointF> pp = new List<PointF>();
            List<DraftElement> last = new List<DraftElement>();
            last.AddRange(elems);
            List<object> accum = new List<object>();
            while (last.Any())
            {
                if (pp.Count == 0)
                {
                    pp.AddRange(last.First().GetPoints());
                    accum.Add(last.First().Tag);
                    last.RemoveAt(0);
                }
                else
                {
                    var ll = pp.Last();
                    var f1 = last.OrderBy(z => Math.Min(z.Start.DistTo(ll), z.End.DistTo(ll))).First();

                    var dist = Math.Min(f1.Start.DistTo(ll), f1.End.DistTo(ll));
                    if (dist > ClosingThreshold)
                    {
                        ret.Add(new LocalContour() { Points = pp.ToList(), Tag = accum.ToArray() });
                        pp.Clear();
                        accum.Clear();
                        continue;
                    }
                    accum.Add(f1.Tag);
                    last.Remove(f1);
                    if (f1.Start.DistTo(ll) < f1.End.DistTo(ll))
                    {
                        pp.AddRange(f1.GetPoints().Skip(1));
                        //pp.Add(f1.End);
                    }
                    else
                    {
                        f1.Reverse();
                        pp.AddRange(f1.GetPoints().Skip(1));
                        //pp.Add(f1.Start);
                    }
                }
            }
            if (pp.Any())
            {
                ret.Add(new LocalContour() { Points = pp.ToList(), Tag = accum.ToArray() });
            }
            return ret.ToArray();
        }
        public static PointF GetPointFromAngle(PointF center, float radius, double angle)
        {
            double y = Math.Sin(angle * Math.PI / 180.0);
            var p1 = new PointF((float)Math.Cos(angle * Math.PI / 180.0), (float)y);
            p1 = new PointF(p1.X * radius, p1.Y * radius);
            p1 = new PointF(p1.X + center.X, p1.Y + center.Y);
            return p1;
        }

    }    
}