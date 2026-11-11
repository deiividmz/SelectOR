// PUESTO DE MANDO: notas del administrador (2026-11-09; sql/puesto-notas.sql). Textos y flechas encima del esquema de una
// dependencia o del trazado real, para los jefes de circulación. Las ven todos; solo el administrador, con clic derecho en
// un sitio vacío, las pone («Añadir texto aquí…», «Añadir una flecha desde aquí…»: el siguiente clic es su final) y, sobre
// una, las cambia o las quita. Cada vista tiene las suyas: el trazado real («board», en metros del tablero) y el esquema de
// cada dependencia (en sus unidades). Se leen cada 20 s (y al guardar). Capa «Notas del administrador».

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        sealed class Note { public long Id; public string View, Kind, Body, Color; public float X1, Y1, X2, Y2; public int Size; public RectangleF Hit; }
        List<Note> _notes = new();
        static bool _showNotes = true;
        DateTime _notesNext = DateTime.MinValue; bool _notesBusy;
        PointF? _arrowFrom; string _arrowView;   // poniendo una flecha: su principio (en la vista) y la vista

        static readonly (string key, string name, Color c)[] NoteColors =
        {
            ("y", "Amarillo", Color.FromArgb(255, 213, 79)), ("w", "Blanco", Color.FromArgb(240, 242, 245)), ("r", "Rojo", Color.FromArgb(239, 83, 80)),
            ("b", "Azul", Color.FromArgb(100, 181, 246)), ("g", "Verde", Color.FromArgb(129, 199, 132)),
        };
        static Color NoteColor(string k) => NoteColors.FirstOrDefault(c => c.key == k).c is var c && c != default ? c : NoteColors[0].c;

        static bool IsAdmin => Supa.IsLoggedIn && Supa.IsSuperadmin;

        // la vista de ahora: «board» o la dependencia (null en el plano general)
        string NoteView => Board ? "board" : Overview ? null : _curZone;

        // (con el resto de capas, cada segundo) leerlas cada 20 s
        void NotesTick(bool now = false)
        {
            if (_notesBusy || (!now && DateTime.UtcNow < _notesNext)) return;
            _notesBusy = true; _notesNext = DateTime.UtcNow.AddSeconds(20);
            _ = Load();
            async Task Load()
            {
                try
                {
                    var (json, err) = await Supa.RpcAsync("room_notes_get", new { p_route = ObjectsRoute });
                    if (err != null || json == null || IsDisposed) return;
                    var l = new List<Note>();
                    using (var d = JsonDocument.Parse(json))
                        foreach (var e in d.RootElement.EnumerateArray())
                        {
                            float F(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : float.NaN;
                            string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                            l.Add(new Note
                            {
                                Id = e.GetProperty("id").GetInt64(), View = S("view"), Kind = S("kind"), Body = S("body"), Color = S("color"),
                                X1 = F("x1"), Y1 = F("y1"), X2 = F("x2"), Y2 = F("y2"), Size = e.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt32() : 2
                            });
                        }
                    _notes = l;
                    _canvas.Invalidate();
                }
                catch { }
                finally { _notesBusy = false; }
            }
        }

        PointF NoteScreen(float x, float y) => Board ? new PointF(_bOrigin.X + x * _bZoom, _bOrigin.Y + y * _bZoom) : new PointF(_origin.X + x * _zoom, _origin.Y + y * _zoom);
        PointF NoteWorld(Point p) => Board ? new PointF((p.X - _bOrigin.X) / _bZoom, (p.Y - _bOrigin.Y) / _bZoom) : new PointF((p.X - _origin.X) / _zoom, (p.Y - _origin.Y) / _zoom);

        // (al dibujar el trazado real o un esquema, encima de todo)
        void DrawNotes(Graphics g, Rectangle area)
        {
            string view = NoteView;
            if (view == null) return;
            var mine = _showNotes ? _notes.Where(n => n.View == view).ToList() : new List<Note>();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var n in mine)
            {
                var col = NoteColor(n.Color);
                float fs = n.Size == 1 ? 8.5f : n.Size == 3 ? 13f : 10.5f;
                var a = NoteScreen(n.X1, n.Y1);
                n.Hit = RectangleF.Empty;
                if (n.Kind == "arrow" && !float.IsNaN(n.X2))
                {
                    var b = NoteScreen(n.X2, n.Y2);
                    float w = n.Size == 1 ? 2f : n.Size == 3 ? 4f : 3f;
                    using (var halo = new Pen(Color.FromArgb(200, 12, 14, 17), w + 3) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(halo, a, b);
                    using (var pen = new Pen(col, w) { StartCap = LineCap.Round, CustomEndCap = new AdjustableArrowCap(3.5f, 4f, true) }) g.DrawLine(pen, a, b);
                    float minX = Math.Min(a.X, b.X), minY = Math.Min(a.Y, b.Y);
                    n.Hit = new RectangleF(minX - 6, minY - 6, Math.Abs(b.X - a.X) + 12, Math.Abs(b.Y - a.Y) + 12);
                }
                if (!string.IsNullOrWhiteSpace(n.Body))
                {
                    using var f = Theme.Font(fs, FontStyle.Bold);
                    var sz = g.MeasureString(n.Body, f, Theme.Px(320));
                    var box = n.Kind == "arrow" ? new RectangleF(a.X - sz.Width / 2 - 5, a.Y - sz.Height - 8, sz.Width + 10, sz.Height + 4)
                                                : new RectangleF(a.X - 5, a.Y - sz.Height / 2 - 2, sz.Width + 10, sz.Height + 4);
                    using (var bg = new SolidBrush(Color.FromArgb(225, 18, 21, 26))) g.FillRectangle(bg, box);
                    using (var bp = new Pen(Color.FromArgb(200, col), 1.2f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                    using (var tb = new SolidBrush(col)) g.DrawString(n.Body, f, tb, new RectangleF(box.X + 5, box.Y + 2, sz.Width + 2, sz.Height + 2));
                    n.Hit = n.Hit.IsEmpty ? box : RectangleF.Union(n.Hit, box);
                }
            }
            // poniendo una flecha: de su principio al ratón
            if (_arrowFrom is PointF af && _arrowView == view)
            {
                var a = NoteScreen(af.X, af.Y); var m = _canvas.PointToClient(Cursor.Position);
                using var pen = new Pen(Color.FromArgb(220, 255, 213, 79), 2.5f) { DashStyle = DashStyle.Dash, CustomEndCap = new AdjustableArrowCap(3.5f, 4f, true) };
                g.DrawLine(pen, a, m);
                using var f = Theme.Font(9f, FontStyle.Bold);
                using var tb = new SolidBrush(Color.FromArgb(255, 213, 79));
                g.DrawString(Tr("Clic en el final de la flecha (Esc: cancelar)"), f, tb, m.X + 12, m.Y + 8);
            }
        }

        Note NoteAt(Point p)
        {
            string view = NoteView; if (view == null || !_showNotes) return null;
            return _notes.LastOrDefault(n => n.View == view && n.Hit.Contains(p));
        }

        // (clic derecho, solo el administrador) añadir, cambiar o quitar
        bool NotesRightClick(Point p)
        {
            if (!IsAdmin || NoteView == null) return false;
            var m = MenuStyle.Apply(new ContextMenuStrip());
            var hit = NoteAt(p);
            if (hit != null)
            {
                m.Items.Add(Tr("Cambiar la nota…"), null, (s, e) => _ = EditNote(hit, PointF.Empty, null));
                m.Items.Add(Tr("Quitar la nota"), null, async (s, e) =>
                {
                    var (_, err) = await Supa.RpcAsync("admin_room_note_delete", new { p_id = hit.Id });
                    if (err != null) { _status.Text = Tr("Error: ") + err; return; }
                    _notes.Remove(hit); _canvas.Invalidate(); NotesTick(true);
                });
            }
            else
            {
                var w = NoteWorld(p);
                m.Items.Add(Tr("Añadir texto aquí…"), null, (s, e) => _ = EditNote(null, w, "text"));
                m.Items.Add(Tr("Añadir una flecha desde aquí…"), null, (s, e) => { _arrowFrom = w; _arrowView = NoteView; _canvas.Invalidate(); });
            }
            m.Show(_canvas, p);
            return true;
        }

        // (clic izquierdo) el final de la flecha que se está poniendo
        bool ArrowClick(Point p)
        {
            if (_arrowFrom is not PointF a) return false;
            if (_arrowView != NoteView) { _arrowFrom = null; return false; }
            var b = NoteWorld(p);
            _arrowFrom = null; _canvas.Invalidate();
            _ = EditNote(null, a, "arrow", b);
            return true;
        }

        bool ArrowCancel() { if (_arrowFrom == null) return false; _arrowFrom = null; _canvas.Invalidate(); return true; }

        async Task EditNote(Note n, PointF at, string kind, PointF? end = null)
        {
            string view = n?.View ?? NoteView; if (view == null) return;
            kind = n?.Kind ?? kind;
            using var dlg = new FormDialog(n == null ? (kind == "arrow" ? Tr("Flecha") : Tr("Nota")) : Tr("Cambiar la nota"), Tr("Guardar"), 520, "📝",
                                           view == "board" ? Tr("Trazado real") : view);
            dlg.AddMultiline("body", kind == "arrow" ? Tr("Texto junto a la flecha (opcional)") : Tr("Texto"), n?.Body ?? "", 80);
            dlg.AddCombo("color", Tr("Color"), NoteColors.Select(c => Tr(c.name)), Math.Max(0, Array.FindIndex(NoteColors, c => c.key == (n?.Color ?? "y"))));
            dlg.AddCombo("size", Tr("Tamaño"), new[] { Tr("Pequeño"), Tr("Mediano"), Tr("Grande") }, Math.Max(0, Math.Min(2, (n?.Size ?? 2) - 1)));
            dlg.AddInfo(Tr("La ven todos los que abren el Puesto de Mando, solo en esta vista."));
            dlg.Validate = async f =>
            {
                string body = f.Get("body").Trim();
                if (kind == "text" && body.Length == 0) return Tr("Escribe el texto.");
                if (body.Length > 200) return Tr("El texto es demasiado largo (200 caracteres como mucho).");
                var (_, err) = await Supa.RpcAsync("admin_room_note_save", new
                {
                    p_id = n?.Id, p_route = ObjectsRoute, p_view = view, p_kind = kind,
                    p_x1 = n?.X1 ?? at.X, p_y1 = n?.Y1 ?? at.Y, p_x2 = n != null ? (float.IsNaN(n.X2) ? (float?)null : n.X2) : end?.X, p_y2 = n != null ? (float.IsNaN(n.Y2) ? (float?)null : n.Y2) : end?.Y,
                    p_body = body, p_color = NoteColors[Math.Max(0, f.Index("color"))].key, p_size = Math.Max(0, f.Index("size")) + 1
                });
                if (err != null) return err.Contains("PGRST202") || err.Contains("Could not find") ? Tr("El servidor aún no tiene las notas del puesto (falta puesto-notas.sql).") : Tr("Error: ") + err;
                return null;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) NotesTick(true);
        }
    }
}
