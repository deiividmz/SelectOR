// Sección «Editor de composiciones»: crea, renombra, edita y elimina archivos .con
// (como el TrainItem Editor de TSRE5, pero con la interfaz de SelectOR).
//   · Izquierda: lista de composiciones de la carpeta de contenido activa.
//   · Centro:    coches del tren (orden, invertir, quitar) y material disponible (añadir).
//   · Abajo:     nombre del tren, guardar/deshacer y vista 2D de la composición editada.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        StyledTable _edList, _edCars, _edStock;
        RoundedInput _edName;
        RoundButton _edSave, _edRevert, _edNew, _edDelete, _edAdd, _edRemove, _edUp, _edDown, _edFlip;
        Label _edMsg, _edTitle, _edCarsHdr;
        TableLayoutPanel _edRoot, _edPreviews, _ed2DCol, _edStats;

        readonly List<string> _edFiles = new();                       // .con de la lista (mismo orden)
        readonly List<string> _edNames = new();                       // nombre visible de cada uno
        readonly List<(string name, string folder, string path, bool isEngine)> _edStockAll = new();
        int _edStockToken;   // rastreos de material en curso (el último manda)
        ConsistDoc _edDoc, _edOriginal;
        bool _edSuppressSel;                                          // ignora la selección mientras se rehace la lista

        // Vistas permanentes: composición 2D del tren + 3D del coche seleccionado
        Panel _ed2DHost; PictureBox _ed2DPic; Label _ed2DInfo, _ed3DTitle;
        TrainPreviewPanel _edPreview3D;
        ShapeGeom _edGeom; string _edGeomPath; float _edYaw = 22, _edPitch = 12; bool _edGeomFlip;
        System.Windows.Forms.Timer _ed2DTimer; int _ed2DToken;
        System.Windows.Forms.Timer _ed3DRerender;   // render 3D tras cambiar de tamaño (una vez, tras un respiro)
        readonly List<(int x0, int x1)> _ed2DSlots = new();          // zona de cada coche en la tira 2D (para el clic)
        static readonly Dictionary<string, ShapeGeom> _geomCache = new(StringComparer.OrdinalIgnoreCase);
        const float Ed2DWorldH = 5.6f;   // altura de encuadre (m) de cada coche
        Label _stCars, _stLen, _stMass, _stPower, _stSpeed, _stBrake, _stCap, _stType;   // datos del tren
        const int Ed2DLabelH = 20;       // franja de etiquetas (nº y nombre) sobre los coches

        // Escala FIJA de la composición 2D: 10 px por metro, en proporción con el resto de la interfaz (UiScale).
        // Antes salía del alto que le quedaba al marco: diminuta en un portátil y enorme en un monitor grande.
        // El marco tiene justo el alto del tren y, si el tren es más largo que el hueco, barra horizontal.
        static float Ed2DPpm() => 10f * Theme.UiScale;
        static int Ed2DFrameH() => (int)(Ed2DWorldH * Ed2DPpm()) + Ed2DLabelH + 6 + 6 + SystemInformation.HorizontalScrollBarHeight;
        // Fila de las vistas 2D/3D: título + marco del 2D (con el relleno de su tarjeta) + datos del tren
        static int EditorStatsH() => Theme.Px(104);
        static int EditorPreviewHeight() => Theme.Px(30) + Ed2DFrameH() + 8 + EditorStatsH();

        // Plazas de viajeros (PassengerCapacity) del coche seleccionado, sobre su .eng/.wag
        RoundedInput _edCap; RoundButton _edCapApply, _edCapClear; Label _edCapLbl; string _edCapPath;
        string _edStockFolder, _edListFolder;                         // carpeta de contenido ya cargada

        // ============================ Construcción de la página ============================
        Panel BuildEditorPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            _edRoot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
            _edRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, EditorListWidth()));
            _edRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _edRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // ---------------- Izquierda: composiciones de la carpeta ----------------
            var leftCard = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, BorderColor = Blend(Theme.Surface, Theme.Bg, 0.35f), Radius = 12, Padding = new Padding(12, 10, 12, 12), Margin = new Padding(0, 0, 10, 0) };
            var leftGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Surface };
            leftGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            leftGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // título
            leftGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buscador
            leftGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // lista
            leftGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // botones
            leftGrid.Controls.Add(EmpHeader("COMPOSICIONES DE LA CARPETA"), 0, 0);

            _edList = EmpTable();
            _edList.SetColumns(
                new StyledTable.Col("TREN", 180, true),
                new StyledTable.Col("COCHES", 80, false, HorizontalAlignment.Right));
            _edList.SelectedIndexChanged += (s, e) => OnEditorConsistSelected();
            leftGrid.Controls.Add(EmpSearch(_edList, 300), 0, 1);
            leftGrid.Controls.Add(_edList, 0, 2);

            var lbtns = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 44, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 0) };
            lbtns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            lbtns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            _edNew = EmpButton(Tr("Nueva composición"), primary: true); _edNew.Dock = DockStyle.Fill; _edNew.Height = 44; _edNew.Margin = new Padding(0, 0, 6, 0);
            _edNew.Click += (s, e) => NewConsist();
            AddAdaptiveText(_edNew, Tr("Nueva composición"), Tr("Nueva"));
            _edDelete = EmpButton(Tr("Eliminar")); _edDelete.Dock = DockStyle.Fill; _edDelete.Height = 44; _edDelete.Margin = new Padding(6, 0, 0, 0);
            _edDelete.BaseColor = Theme.Surface2; _edDelete.HoverColor = Color.FromArgb(150, 60, 60); _edDelete.TextColor = RedC;
            _edDelete.Click += (s, e) => DeleteConsist();
            lbtns.Controls.Add(_edNew, 0, 0); lbtns.Controls.Add(_edDelete, 1, 0);
            leftGrid.Controls.Add(lbtns, 0, 3);
            leftCard.Controls.Add(leftGrid);
            _edRoot.Controls.Add(leftCard, 0, 0);

            // ---------------- Derecha: edición del tren ----------------
            var rightCard = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, BorderColor = Blend(Theme.Surface, Theme.Bg, 0.35f), Radius = 12, Padding = new Padding(14, 10, 14, 12) };
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Surface };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // cabecera + nombre
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // coches | acciones | material
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, EditorPreviewHeight()));   // vistas 2D y 3D
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // botones
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // mensajes

            // Cabecera: título + nombre del tren + renombrar archivo
            var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 2, BackColor = Theme.Surface };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            _edTitle = new Label { Text = Tr("Elige una composición"), AutoSize = true, ForeColor = Theme.Text, Font = Theme.Font(12.5f, FontStyle.Bold), Margin = new Padding(2, 2, 2, 2) };
            head.Controls.Add(_edTitle, 0, 0);
            // Avisos («guardado», «añadido», errores…) en la misma línea del título, a la derecha.
            _edMsg = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Subtle, Font = Theme.Font(9f), TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, Margin = new Padding(8, 4, 2, 2) };
            head.Controls.Add(_edMsg, 1, 0);
            head.SetColumnSpan(_edMsg, 2);
            var nameLbl = EmpFieldLabel(Tr("Nombre del tren (el que se ve en SelectOR y en Open Rails)"));
            head.Controls.Add(nameLbl, 0, 1);
            head.SetColumnSpan(nameLbl, 3);
            _edName = EmpInput("");
            _edName.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _edName.Box.TextChanged += (s, e) => { if (_edDoc != null && _edDoc.DisplayName != _edName.Box.Text) { _edDoc.DisplayName = _edName.Box.Text; UpdateEditorDirty(); } };
            // Nombre del tren + plazas del coche elegido, en la misma línea.
            var nameRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0) };
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            _edCapLbl = new Label { Text = Tr("Plazas de viajeros"), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f), Margin = new Padding(14, 9, 6, 2) };
            _edCap = EmpInput("0"); _edCap.Anchor = AnchorStyles.Left | AnchorStyles.Right; _edCap.Margin = new Padding(2, 0, 6, 0);
            _edCapApply = EmpButton(Tr("Aplicar"), primary: true); _edCapApply.Dock = DockStyle.Fill; _edCapApply.Height = 36; _edCapApply.Margin = new Padding(0, 0, 6, 0);
            _edCapApply.Click += (s, e) => ApplyCarCapacity(false);
            _edCapClear = EmpButton(Tr("Quitar")); _edCapClear.Dock = DockStyle.Fill; _edCapClear.Height = 36; _edCapClear.Margin = new Padding(0);
            _edCapClear.Click += (s, e) => ApplyCarCapacity(true);
            nameRow.Controls.Add(_edName, 0, 0);
            nameRow.Controls.Add(_edCapLbl, 1, 0);
            nameRow.Controls.Add(_edCap, 2, 0);
            nameRow.Controls.Add(_edCapApply, 3, 0);
            nameRow.Controls.Add(_edCapClear, 4, 0);
            var headHost = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 6) };
            headHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headHost.Controls.Add(head, 0, 0);
            headHost.Controls.Add(nameRow, 0, 1);
            right.Controls.Add(headHost, 0, 0);

            // Centro: coches del tren | acciones | material disponible
            var mid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Theme.Surface };
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));

            var carsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface };
            carsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            carsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            carsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _edCarsHdr = EmpHeader("COCHES DEL TREN");
            carsGrid.Controls.Add(_edCarsHdr, 0, 0);
            _edCars = EmpTable();
            _edCars.SetColumns(
                new StyledTable.Col("#", 34, false, HorizontalAlignment.Right),
                new StyledTable.Col("COCHE", 150, true),
                new StyledTable.Col("CARPETA", 120),
                new StyledTable.Col("TIPO", 90),
                new StyledTable.Col("SENTIDO", 90));
            _edCars.MouseDoubleClick += (s, e) => FlipCar();
            _edCars.SelectedIndexChanged += (s, e) => OnEditorCarSelected();
            // volver a pulsar el coche ya elegido (tras mirar material disponible) lo enseña otra vez en 3D
            _edCars.MouseClick += (s, e) => { int i = _edCars.SelectedRow; if (i >= 0 && _ed3DSource != "car" + i) Show3DCar(i); };
            carsGrid.Controls.Add(_edCars, 0, 1);
            mid.Controls.Add(carsGrid, 0, 0);

            // Columna de acciones (entre las dos listas)
            var acts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Surface, Padding = new Padding(8, 30, 8, 0) };
            acts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) acts.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _edAdd = EmpButton(Tr("◀  Añadir"), primary: true); _edAdd.Dock = DockStyle.Top; _edAdd.Height = 38; _edAdd.Margin = new Padding(0, 0, 0, 10);
            _edAdd.Click += (s, e) => AddCarFromStock();
            _edRemove = EmpButton(Tr("Quitar  ▶")); _edRemove.Dock = DockStyle.Top; _edRemove.Height = 38; _edRemove.Margin = new Padding(0, 0, 0, 18);
            _edRemove.Click += (s, e) => RemoveCar();
            _edUp = EmpButton(Tr("▲  Subir")); _edUp.Dock = DockStyle.Top; _edUp.Height = 38; _edUp.Margin = new Padding(0, 0, 0, 8);
            _edUp.Click += (s, e) => MoveCar(-1);
            _edDown = EmpButton(Tr("▼  Bajar")); _edDown.Dock = DockStyle.Top; _edDown.Height = 38; _edDown.Margin = new Padding(0, 0, 0, 18);
            _edDown.Click += (s, e) => MoveCar(1);
            _edFlip = EmpButton(Tr("⇄  Invertir")); _edFlip.Dock = DockStyle.Top; _edFlip.Height = 38; _edFlip.Margin = new Padding(0, 0, 0, 8);
            _edFlip.Click += (s, e) => FlipCar();
            acts.Controls.Add(_edAdd, 0, 0); acts.Controls.Add(_edRemove, 0, 1);
            acts.Controls.Add(_edUp, 0, 2); acts.Controls.Add(_edDown, 0, 3);
            acts.Controls.Add(_edFlip, 0, 4);
            mid.Controls.Add(acts, 1, 0);

            var stockGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Surface };
            stockGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stockGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stockGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stockGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stockGrid.Controls.Add(EmpHeader("MATERIAL DISPONIBLE (TRAINSET)"), 0, 0);
            _edStock = EmpTable();
            _edStock.SetColumns(
                new StyledTable.Col("MATERIAL", 150, true),
                new StyledTable.Col("CARPETA", 130),
                new StyledTable.Col("TIPO", 90));
            _edStock.MouseDoubleClick += (s, e) => AddCarFromStock();
            // un clic en el material disponible lo enseña en la vista 3D (sin añadirlo al tren)
            _edStock.SelectedIndexChanged += (s, e) => Show3DStock(_edStock.SelectedRow);
            _edStock.MouseClick += (s, e) => Show3DStock(_edStock.SelectedRow);
            stockGrid.Controls.Add(EmpSearch(_edStock, 280), 0, 1);
            stockGrid.Controls.Add(_edStock, 0, 2);
            mid.Controls.Add(stockGrid, 2, 0);
            right.Controls.Add(mid, 0, 1);

            // ---------------- Vistas permanentes: 2D del tren + 3D del coche ----------------
            _edPreviews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 0) };
            _edPreviews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _edPreviews.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, EditorPreview3DWidth()));

            var comp2D = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = new Padding(0, 0, 12, 0) };
            _ed2DCol = comp2D;
            comp2D.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            comp2D.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            comp2D.RowStyles.Add(new RowStyle(SizeType.Absolute, Ed2DFrameH() + 8));   // marco del tren (alto fijo)
            _ed2DInfo = EmpHeader("COMPOSICIÓN 2D");
            comp2D.Controls.Add(_ed2DInfo, 0, 0);
            var card2D = new Card { Dock = DockStyle.Fill, Fill = Theme.Bg, BorderColor = Blend(Theme.Surface, Theme.Bg, 0.35f), Radius = 10, Padding = new Padding(4) };
            _ed2DHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
            Native.UseDarkScrollBars(_ed2DHost);
            _ed2DPic = new PictureBox { SizeMode = PictureBoxSizeMode.AutoSize, BackColor = Color.Transparent, Location = new Point(6, 6) };
            _ed2DPic.MouseDown += (s, e) => PickCarFrom2D(e.X);
            _ed2DHost.Controls.Add(_ed2DPic);
            card2D.Controls.Add(_ed2DHost);
            comp2D.Controls.Add(card2D, 0, 1);

            // Datos del tren completo, en el hueco que queda bajo la composición 2D.
            var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 0) };
            for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            stats.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            stats.Controls.Add(FleetChip("COCHES", out _stCars), 0, 0);
            stats.Controls.Add(FleetChip("LONGITUD", out _stLen), 1, 0);
            stats.Controls.Add(FleetChip("MASA", out _stMass), 2, 0);
            stats.Controls.Add(FleetChip("FRENO", out _stBrake), 3, 0);
            stats.Controls.Add(FleetChip("POTENCIA", out _stPower), 0, 1);
            stats.Controls.Add(FleetChip("VELOCIDAD MÁX.", out _stSpeed), 1, 1);
            stats.Controls.Add(FleetChip("PLAZAS", out _stCap), 2, 1);
            stats.Controls.Add(FleetChip("TIPO", out _stType), 3, 1);
            comp2D.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // datos del tren: el resto
            comp2D.RowCount = 3;
            comp2D.Controls.Add(stats, 0, 2);
            _edStats = stats;

            _edPreviews.Controls.Add(comp2D, 0, 0);

            var view3D = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface };
            view3D.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            view3D.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            view3D.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _ed3DTitle = EmpHeader("VISTA 3D DEL COCHE");
            view3D.Controls.Add(_ed3DTitle, 0, 0);
            var card3D = new Card { Dock = DockStyle.Fill, Fill = Theme.Bg, BorderColor = Blend(Theme.Surface, Theme.Bg, 0.35f), Radius = 10, Padding = new Padding(4) };
            _edPreview3D = new TrainPreviewPanel { Dock = DockStyle.Fill };
            _edPreview3D.Dragged += (dx, dy) => { if (_edGeom != null) { _edYaw += dx * 0.6f; _edPitch = Math.Max(-55f, Math.Min(70f, _edPitch - dy * 0.6f)); Render3DLive(); } };
            _edPreview3D.ResetRequested += () => { _edYaw = 22; _edPitch = 12; Render3DLive(); };
            _edPreview3D.Zoomed += () => { if (_edGeom != null) Render3DLive(); };
            _ed3DRerender = new System.Windows.Forms.Timer { Interval = 140 };
            _ed3DRerender.Tick += (s, e) => { _ed3DRerender.Stop(); Render3DLive(); };
            _edPreview3D.Resize += (s, e) => { if (_edGeom != null) { _ed3DRerender.Stop(); _ed3DRerender.Start(); } };
            card3D.Controls.Add(_edPreview3D);
            view3D.Controls.Add(card3D, 0, 1);

            _edPreviews.Controls.Add(view3D, 1, 0);
            right.Controls.Add(_edPreviews, 0, 2);

            // Guardar / deshacer
            var sbtns = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 46, ColumnCount = 3, RowCount = 1, BackColor = Theme.Surface, Margin = new Padding(0, 10, 0, 0) };
            sbtns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            sbtns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            sbtns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _edSave = EmpButton(Tr("Guardar cambios"), primary: true); _edSave.Dock = DockStyle.Fill; _edSave.Height = 44; _edSave.Margin = new Padding(0, 0, 8, 0);
            _edSave.Click += (s, e) => SaveConsistDoc();
            _edRevert = EmpButton(Tr("Deshacer cambios")); _edRevert.Dock = DockStyle.Fill; _edRevert.Height = 44; _edRevert.Margin = new Padding(0, 0, 8, 0);
            _edRevert.Click += (s, e) => RevertConsistDoc();
            sbtns.Controls.Add(_edSave, 0, 0); sbtns.Controls.Add(_edRevert, 1, 0);
            right.Controls.Add(sbtns, 0, 3);



            rightCard.Controls.Add(right);
            _edRoot.Controls.Add(rightCard, 1, 0);
            page.Controls.Add(_edRoot);
            SetEditorEnabled(false);
            return page;
        }

        // Medidas proporcionales al tamaño REAL de la ventana (con topes), para que la sección se
        // aproveche igual en un portátil de 1366×768 que en un monitor grande.
        int EditorListWidth() => Clamp((int)(ClientSize.Width * 0.24), Theme.Px(320), Theme.Px(440));
        int EditorPreview3DWidth() => Clamp((int)(ClientSize.Width * 0.33), Theme.Px(300), Theme.Px(620));
        static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        void ApplyEditorSize()
        {
            if (_edRoot != null && _edRoot.ColumnStyles.Count > 0)
            {
                float w = EditorListWidth();
                if (_edRoot.ColumnStyles[0].Width != w) _edRoot.ColumnStyles[0].Width = w;
            }
            if (_edPreviews != null)
            {
                float w3 = EditorPreview3DWidth();
                if (_edPreviews.ColumnStyles.Count > 1 && _edPreviews.ColumnStyles[1].Width != w3) _edPreviews.ColumnStyles[1].Width = w3;
            }
        }

        // ============================ Carga de listas ============================
        // Tipo del vehículo tal y como lo declara su archivo; si no lo declara, lo que se puede saber
        // por la extensión (.eng = motriz, .wag = remolcado).
        string VehicleKindLabel(string path, bool isEngine)
        {
            string k = VehicleTypeShort(DeclaredVehicleType(path));
            return k.Length > 0 ? k : (isEngine ? Tr("Tracción") : Tr("Remolcado"));
        }

        void OnEditorShown()
        {
            // Las tablas se rellenan con la pestaña CERRADA. Si su ventana nativa no existe todavía,
            // Windows se guarda las filas y las vuelca de golpe al abrir la sección por primera vez
            // (con miles de composiciones eso son segundos de espera). Creándola aquí, ese coste se
            // paga ahora, en segundo plano, y abrir la pestaña es inmediato.
            EnsureEditorTableHandles();
            string root = _curFolder?.Path ?? "";
            if (root.Length == 0)
            {
                // Al abrir el programa la carpeta todavía puede estar resolviéndose: eso es cargar,
                // no falta de contenido (OnFolderChanged vuelve a llamar aquí en cuanto la hay).
                string msg = Tr(_noContent ? "Selecciona una carpeta de contenido." : "Cargando…");
                _edList?.SetEmpty(msg);
                _edStock?.SetEmpty(msg);
                return;
            }
            if (_edStockFolder != root) LoadEditorStock(root);
            if (_edListFolder != root || _edFiles.Count == 0) LoadEditorConsists();
        }

        // La carpeta de contenido (o su lista de trenes) ha cambiado: si el editor está a la vista, se recarga.
        void EnsureEditorTableHandles()
        {
            try
            {
                if (!IsHandleCreated) return;
                if (_edList != null && !_edList.IsHandleCreated) { _ = _edList.Handle; }
                if (_edStock != null && !_edStock.IsHandleCreated) { _ = _edStock.Handle; }
                if (_edCars != null && !_edCars.IsHandleCreated) { _ = _edCars.Handle; }
            }
            catch { }
        }

        // Los trenes del contenido ya están: se deja el editor LISTO aunque la pestaña no esté abierta,
        // para que al entrar no haya que cargar nada (es el trabajo pesado de la sección).
        void EditorContentChanged()
        {
            if (_edList == null) return;
            _edStockFolder = null; _edListFolder = null;
            // Al arrancar, las listas del editor (miles de filas) se rellenan a trozos con el menú ya
            // abierto: meterlas de golpe hacía esperar más de un segundo a la pantalla de inicio.
            _edChunkedFill = !_uiRevealed;
            try { OnEditorShown(); } finally { _edChunkedFill = false; }
        }

        bool _edChunkedFill;   // la próxima carga de la lista de composiciones va a trozos
        int _edListFill;       // relleno a trozos en marcha (si cambia, se abandona)

        // El material disponible se rastrea en segundo plano (son miles de carpetas): mientras tanto
        // la pestaña enseña la animación de carga en vez de congelarse.
        void LoadEditorStock(string root)
        {
            _edStockAll.Clear();
            _edStockFolder = root;
            int token = ++_edStockToken;
            _edStock?.SetEmpty(Tr("Cargando…"));

            // Al abrir el programa directamente en esta pestaña aún no hay ventana a la que volver:
            // en ese caso se rastrea aquí mismo (es el arranque, no hay nada que congelar).
            if (!IsHandleCreated)
            {
                _edStockAll.AddRange(ScanEditorStock(root));
                RefreshStockTable();
                LoadStep("stock");
                return;
            }

            Task.Run(() =>
            {
                LoadLog("editor: recorriendo TRAINSET…");
                var list = ScanEditorStock(root);
                LoadLog($"editor: {list.Count} vehículos encontrados");
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (token != _edStockToken) return;
                        _edStockAll.Clear();
                        _edStockAll.AddRange(list);
                        // Con decenas de miles de vehículos, meterlos en la tabla son segundos. Se hace ANTES de
                        // abrir el menú (la pantalla de inicio va en su propio hilo y espera al paso «stock»): con
                        // el menú ya abierto, ese relleno le quitaba soltura los primeros segundos.
                        FillStockTableChunked(token);
                    }));
                }
                catch { }
            });
        }

        // Todos los .eng/.wag de <contenido>\TRAINS\TRAINSET, ordenados por carpeta y nombre.
        static List<(string name, string folder, string path, bool isEngine)> ScanEditorStock(string root)
        {
            // Si el índice ya ha recorrido TRAINSET al calcular su huella, se aprovecha esa lista.
            var list = ContentIndex.StockFor(root);
            if (list != null)
            {
                list.Sort((x, y) => string.Compare(x.folder + "/" + x.name, y.folder + "/" + y.name, StringComparison.CurrentCultureIgnoreCase));
                return list;
            }
            list = new List<(string name, string folder, string path, bool isEngine)>();
            try
            {
                var trainset = Path.Combine(root, "TRAINS", "TRAINSET");
                if (Directory.Exists(trainset))
                    foreach (var dir in Directory.GetDirectories(trainset))
                    {
                        string folder = Path.GetFileName(dir);
                        foreach (var f in Directory.GetFiles(dir, "*.eng"))
                            list.Add((Path.GetFileNameWithoutExtension(f), folder, f, true));
                        foreach (var f in Directory.GetFiles(dir, "*.wag"))
                            list.Add((Path.GetFileNameWithoutExtension(f), folder, f, false));
                    }
            }
            catch { }
            list.Sort((x, y) => string.Compare(x.folder + "/" + x.name, y.folder + "/" + y.name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        // Relleno a trozos (unos 30 ms cada vez, dejando respirar a la interfaz entre uno y otro). Si
        // mientras tanto se rehace la tabla entera (RefreshStockTable), este relleno se abandona.
        int _edStockFill;

        void FillStockTableChunked(int token)
        {
            if (_edStock == null) { LoadStep("stock"); return; }
            int gen = ++_edStockFill;
            _edStock.ClearRows();
            if (_edStockAll.Count == 0) { _edStock.SetEmpty(Tr("No hay material en TRAINS\\TRAINSET.")); LoadStep("stock"); return; }
            var items = new List<(string name, string folder, string path, bool isEngine)>(_edStockAll);
            int next = 0, chunk = 200;
            var t = new System.Windows.Forms.Timer { Interval = 1 };
            t.Tick += (s, e) =>
            {
                // (abandonado: el que lo sustituye ya está o lo hará; la pantalla de inicio no se queda esperando)
                if (token != _edStockToken || gen != _edStockFill || IsDisposed) { t.Stop(); t.Dispose(); LoadStep("stock"); return; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _edStock.BeginUpdate();
                int end = Math.Min(items.Count, next + chunk);
                for (; next < end; next++)
                {
                    var st = items[next];
                    _edStock.AddRow(new[] { st.name, st.folder, VehicleKindLabel(st.path, st.isEngine) },
                                    new Color?[] { null, Theme.Subtle, st.isEngine ? Theme.AccentHi : Theme.Subtle });
                }
                _edStock.EndUpdate();   // aquí es donde Windows mete de verdad las filas
                // con el menú aún oculto, trozos más grandes (nada que dibujar ni que atender): acaba antes
                chunk = NextChunk(chunk, sw.ElapsedMilliseconds, _uiRevealed ? 30 : 250);
                if (next >= items.Count) { t.Stop(); t.Dispose(); LoadLog("editor: tabla de material rellenada"); LoadStep("stock"); }
            };
            t.Start();
        }

        void RefreshStockTable()
        {
            if (_edStock == null) return;
            _edStockFill++;   // cancela un relleno a trozos en marcha
            _edStock.ClearRows();
            _edStock.BeginUpdate();
            foreach (var s in _edStockAll)
                _edStock.AddRow(new[] { s.name, s.folder, VehicleKindLabel(s.path, s.isEngine) },
                                new Color?[] { null, Theme.Subtle, s.isEngine ? Theme.AccentHi : Theme.Subtle });
            _edStock.EndUpdate();
            if (_edStockAll.Count == 0) _edStock.SetEmpty(Tr("No hay material en TRAINS\\TRAINSET."));
        }

        void LoadEditorConsists(string selectPath = null)
        {
            // Aviso a la pantalla de inicio cuando la lista queda hecha (si los trenes aún vienen de
            // camino no cuenta: se vuelve a llamar aquí en cuanto llegan).
            try { LoadEditorConsistsCore(selectPath); }
            finally { if (!_consistsLoading) LoadStep("editor"); }
        }

        void LoadEditorConsistsCore(string selectPath)
        {
            if (_edList == null) return;
            _edSuppressSel = true;
            _edFiles.Clear(); _edNames.Clear(); _edList.ClearRows();
            string root = _curFolder?.Path ?? "";
            string dir = root.Length > 0 ? Path.Combine(root, "TRAINS", "CONSISTS") : "";
            if (dir.Length == 0 || !Directory.Exists(dir)) { _edList.SetEmpty(Tr("Esta carpeta de contenido no tiene TRAINS\\CONSISTS.")); _edSuppressSel = false; return; }
            // Nombre visible: el que ya conoce SelectOR (lista de trenes); si no, el del archivo.
            var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _consistsAll) if (c?.FilePath != null) byPath[c.FilePath] = c.Name;
            // Si los trenes del contenido aún están en camino, la lista se llenaría con los nombres de
            // archivo y se recargaría al llegar: mejor avisar aquí y esperar (EditorContentChanged).
            if (_consistsLoading)
            {
                _edList.SetEmpty(Tr("Cargando…"));
                _edListFolder = null;
                _edSuppressSel = false;
                return;
            }
            var files = new List<string>(Directory.GetFiles(dir, "*.con"));
            files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.CurrentCultureIgnoreCase));
            foreach (var f in files)
            {
                string name = byPath.TryGetValue(f, out var n) && !string.IsNullOrWhiteSpace(n) ? n : Path.GetFileNameWithoutExtension(f);
                _edFiles.Add(f); _edNames.Add(name);
            }
            int gen = ++_edListFill;
            if (_edChunkedFill && selectPath == null && _edFiles.Count > 0)
            {
                _edListFolder = root;
                _edSuppressSel = false;
                FillEditorListChunked(gen, root);
                return;
            }
            _edList.BeginUpdate();
            for (int i = 0; i < _edFiles.Count; i++)
                _edList.AddRow(new[] { _edNames[i], "…" }, new Color?[] { null, Theme.Subtle });
            _edList.EndUpdate();
            _edListFolder = root;
            if (_edFiles.Count == 0) _edList.SetEmpty(Tr("No hay composiciones en esta carpeta."));
            else FillConsistCarCounts();   // nº de coches de cada tren (en segundo plano)
            int sel = selectPath == null ? -1 : _edFiles.FindIndex(p => string.Equals(p, selectPath, StringComparison.OrdinalIgnoreCase));
            if (sel >= 0) _edList.SelectRow(sel);
            // El ListView aún puede mover la selección al repintarse: se abre el tren pedido DESPUÉS.
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _edSuppressSel = false;
                    if (sel >= 0) { _edList.SelectRow(sel); OpenConsistDoc(_edFiles[sel]); }
                }));
            }
            catch { _edSuppressSel = false; }
        }

        // Tamaño del siguiente trozo para que cada uno dure unos 30 ms (contando lo que tarda Windows en
        // meter las filas al reanudar el repintado): la interfaz responde entre trozo y trozo.
        static int NextChunk(int chunk, long ms, int targetMs = 30)
        {
            int max = targetMs > 30 ? 20000 : 4000;
            if (ms <= 0) return Math.Min(max, chunk * 2);
            int ideal = (int)(chunk * (double)targetMs / ms);
            return Math.Max(50, Math.Min(max, Math.Min(chunk * 2, ideal)));
        }

        // Lista de composiciones a trozos (unos 30 ms cada vez) y, al terminar, el nº de coches.
        void FillEditorListChunked(int gen, string root)
        {
            int next = 0, chunk = 200;
            var t = new System.Windows.Forms.Timer { Interval = 1 };
            t.Tick += (s, e) =>
            {
                if (gen != _edListFill || _edListFolder != root || IsDisposed) { t.Stop(); t.Dispose(); return; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _edList.BeginUpdate();
                int end = Math.Min(_edFiles.Count, next + chunk);
                for (; next < end; next++)
                    _edList.AddRow(new[] { _edNames[next], "…" }, new Color?[] { null, Theme.Subtle });
                _edList.EndUpdate();   // aquí es donde Windows mete de verdad las filas
                chunk = NextChunk(chunk, sw.ElapsedMilliseconds);
                if (next >= _edFiles.Count) { t.Stop(); t.Dispose(); FillConsistCarCounts(); }
            };
            t.Start();
        }

        void OnEditorConsistSelected()
        {
            if (_edSuppressSel) return;
            int i = _edList?.SelectedRow ?? -1;
            if (i < 0 || i >= _edFiles.Count) return;
            if (_edDoc != null && _edOriginal != null && !_edDoc.SameAs(_edOriginal))
            {
                var r = MessageBox.Show(this, Tr("Hay cambios sin guardar en esta composición. ¿Guardarlos antes de cambiar de tren?"),
                    "SelectOR", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.Yes) SaveConsistDoc(silent: true);
            }
            OpenConsistDoc(_edFiles[i]);
        }

        void OpenConsistDoc(string path)
        {
            _edDoc = ConsistDoc.Load(path);
            if (_edDoc == null)
            {
                SetEditorEnabled(false);
                Msg(_edMsg, Tr("No se pudo leer el archivo de composición."), true);
                return;
            }
            _edOriginal = _edDoc.Clone();
            _edName.Box.Text = _edDoc.DisplayName ?? "";
            SetEditorEnabled(true);
            RefreshCarTable();
            Msg(_edMsg, Path.GetFileName(path), false);
        }

        void RefreshCarTable(int select = -1)
        {
            if (_edCars == null) return;
            _edCars.ClearRows();
            if (_edDoc != null)
                for (int i = 0; i < _edDoc.Cars.Count; i++)
                {
                    var c = _edDoc.Cars[i];
                    bool exists = ResolveCarFile(c.Name, c.Folder) != null;
                    _edCars.AddRow(
                        new[] { (i + 1).ToString(), c.Name, c.Folder, VehicleKindLabel(ResolveCarFile(c.Name, c.Folder), c.IsEngine), c.Flip ? Tr("Invertido") : Tr("Normal") },
                        new Color?[] { Theme.Subtle, exists ? (Color?)null : RedC, Theme.Subtle, c.IsEngine ? Theme.AccentHi : Theme.Subtle, c.Flip ? ColOrange : Theme.Subtle });
                }
            if (_edDoc != null && _edDoc.Cars.Count == 0) _edCars.SetEmpty(Tr("Tren vacío: añade coches desde la lista de la derecha."));
            _edCarsHdr.Text = Tr("COCHES DEL TREN") + (_edDoc != null ? "  ·  " + _edDoc.Cars.Count : "");
            UpdateEditorDirty();
            UpdateEditorStats();   // tarjetas al momento; la tira 2D llega después
            Queue2DRender();
            if (select >= 0) _edCars.SelectRow(select);
        }

        void SetEditorEnabled(bool on)
        {
            foreach (var b in new[] { _edSave, _edRevert, _edAdd, _edRemove, _edUp, _edDown, _edFlip, _edCapApply, _edCapClear })
                if (b != null) b.Enabled = on;
            if (_edName != null) _edName.Enabled = on;
            if (!on)
            {
                _edCars?.ClearRows();
                if (_edCars != null) _edCars.SetEmpty(Tr("Elige una composición de la lista."));
                if (_edTitle != null) _edTitle.Text = Tr("Elige una composición");
                if (_edName != null) _edName.Box.Text = "";
                _edDoc = null; _edOriginal = null;
                Show3DCar(-1);
                ShowCarCapacity(-1);
                UpdateEditorStats();
                Queue2DRender();
            }
        }

        void UpdateEditorDirty()
        {
            bool dirty = _edDoc != null && _edOriginal != null && !_edDoc.SameAs(_edOriginal);
            if (_edTitle != null && _edDoc != null)
                _edTitle.Text = (string.IsNullOrWhiteSpace(_edDoc.DisplayName) ? Path.GetFileNameWithoutExtension(_edDoc.Path) : _edDoc.DisplayName)
                                + (dirty ? "  •" : "");
            if (_edSave != null) { _edSave.Enabled = dirty; _edSave.Invalidate(); }
            if (_edRevert != null) { _edRevert.Enabled = dirty; _edRevert.Invalidate(); }
        }

        // ============================ Edición de coches ============================
        void AddCarFromStock()
        {
            if (_edDoc == null) return;
            int s = _edStock?.SelectedRow ?? -1;
            if (s < 0 || s >= _edStockAll.Count) { Msg(_edMsg, Tr("Elige un vehículo del material disponible."), true); return; }
            var it = _edStockAll[s];
            int at = _edCars?.SelectedRow ?? -1;
            var car = new ConsistCar { Name = it.name, Folder = it.folder, IsEngine = it.isEngine, Flip = false };
            int pos = at >= 0 && at < _edDoc.Cars.Count ? at + 1 : _edDoc.Cars.Count;   // tras el coche elegido
            _edDoc.Cars.Insert(pos, car);
            RefreshCarTable(pos);
            Msg(_edMsg, string.Format(Tr("Añadido: {0}"), it.name), false);
        }

        void RemoveCar()
        {
            if (_edDoc == null) return;
            int i = _edCars?.SelectedRow ?? -1;
            if (i < 0 || i >= _edDoc.Cars.Count) { Msg(_edMsg, Tr("Elige un coche del tren."), true); return; }
            string n = _edDoc.Cars[i].Name;
            _edDoc.Cars.RemoveAt(i);
            RefreshCarTable(Math.Min(i, _edDoc.Cars.Count - 1));
            Msg(_edMsg, string.Format(Tr("Quitado: {0}"), n), false);
        }

        void MoveCar(int delta)
        {
            if (_edDoc == null) return;
            int i = _edCars?.SelectedRow ?? -1;
            int j = i + delta;
            if (i < 0 || i >= _edDoc.Cars.Count || j < 0 || j >= _edDoc.Cars.Count) return;
            var c = _edDoc.Cars[i];
            _edDoc.Cars.RemoveAt(i);
            _edDoc.Cars.Insert(j, c);
            RefreshCarTable(j);
        }

        void FlipCar()
        {
            if (_edDoc == null) return;
            int i = _edCars?.SelectedRow ?? -1;
            if (i < 0 || i >= _edDoc.Cars.Count) return;
            _edDoc.Cars[i].Flip = !_edDoc.Cars[i].Flip;
            RefreshCarTable(i);
        }

        // ============================ Guardar / deshacer ============================
        void SaveConsistDoc(bool silent = false)
        {
            if (_edDoc == null) return;
            if (_edDoc.Cars.Count == 0) { Msg(_edMsg, Tr("El tren no tiene ningún coche: añade al menos uno."), true); return; }
            _edDoc.DisplayName = (_edName.Box.Text ?? "").Trim();
            if (_edDoc.DisplayName.Length == 0) _edDoc.DisplayName = Path.GetFileNameWithoutExtension(_edDoc.Path);
            // Al cambiar el nombre del tren, el identificador de TrainCfg ( … ) lo sigue, igual que el
            // Name y el nombre del archivo (si no se ha renombrado, se respeta el que traía).
            if (_edOriginal == null || !string.Equals(_edOriginal.DisplayName, _edDoc.DisplayName, StringComparison.Ordinal))
                _edDoc.Id = _edDoc.DisplayName;
            string before = _edDoc.Path;
            string err = _edDoc.Save();
            ClearContentCaches();   // el .con ha cambiado: sus datos se releerán
            if (err != null) { Msg(_edMsg, Tr("No se pudo guardar: ") + err, true); return; }
            // El archivo .con acompaña al nombre del tren: si cambia, se renombra solo.
            string renamed = SyncFileNameToTrainName();
            _edOriginal = _edDoc.Clone();
            UpdateEditorDirty();
            ReloadConsistsAfterEdit(changed: new[] { before, _edDoc.Path });   // Exploración: datos, ficha y 2D al día
            if (renamed != null) LoadEditorConsists(_edDoc.Path);   // la lista debe apuntar al archivo nuevo
            if (!silent)
                Msg(_edMsg, renamed != null
                    ? string.Format(Tr("Guardado y archivo renombrado a {0}"), renamed)
                    : string.Format(Tr("Guardado: {0}"), Path.GetFileName(_edDoc.Path)), false);
        }

        void RevertConsistDoc()
        {
            if (_edDoc?.Path == null) return;
            OpenConsistDoc(_edDoc.Path);
            Msg(_edMsg, Tr("Cambios descartados."), false);
        }

        // ============================ Archivo: nueva / renombrar / eliminar ============================
        void NewConsist()
        {
            string root = _curFolder?.Path ?? "";
            string dir = root.Length > 0 ? Path.Combine(root, "TRAINS", "CONSISTS") : "";
            if (dir.Length == 0 || !Directory.Exists(dir)) { Msg(_edMsg, Tr("Esta carpeta de contenido no tiene TRAINS\\CONSISTS."), true); return; }
            string name;
            using (var dlg = new TextPromptDialog(Tr("Nueva composición"), Tr("Nombre del tren"), "", Tr("Mi tren")))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                name = (dlg.Value ?? "").Trim();
            }
            if (name.Length == 0) return;
            string file = Path.Combine(dir, SafeFileName(name) + ".con");
            if (File.Exists(file)) { Msg(_edMsg, Tr("Ya existe una composición con ese nombre de archivo."), true); return; }
            var doc = new ConsistDoc { Path = file, Id = name, DisplayName = name };
            string err = doc.Save(file);
            ClearContentCaches();
            if (err != null) { Msg(_edMsg, Tr("No se pudo crear: ") + err, true); return; }
            ReloadConsistsAfterEdit(changed: new[] { file });
            LoadEditorConsists(file);
            Msg(_edMsg, Tr("Composición creada: añade coches y pulsa Guardar cambios."), false);
        }

        // Mantiene el nombre del ARCHIVO .con igual que el nombre del tren (al guardar).
        // Devuelve el nombre nuevo si lo renombró, o null si no hizo falta.
        string SyncFileNameToTrainName()
        {
            try
            {
                string old = _edDoc?.Path;
                if (old == null) return null;
                string want = SafeFileName(_edDoc.DisplayName ?? "");
                if (want.Length == 0) return null;
                string cur = Path.GetFileNameWithoutExtension(old);
                if (string.Equals(want, cur, StringComparison.OrdinalIgnoreCase)) return null;
                string dst = Path.Combine(Path.GetDirectoryName(old) ?? "", want + ".con");
                if (File.Exists(dst)) return null;   // ya existe otro con ese nombre: se deja como estaba
                File.Move(old, dst);
                _edDoc.Path = dst;
                if (_edOriginal != null) _edOriginal.Path = dst;
                return Path.GetFileName(dst);
            }
            catch { return null; }
        }

        // Rellena la columna COCHES de la lista (nº de vehículos de cada .con), en segundo plano.
        // Rellena la columna COCHES leyendo cada .con en segundo plano; hasta que llega, cada tren
        // muestra «…» en esa columna (la sección ya es usable).
        async void FillConsistCarCounts()
        {
            var files = new List<string>(_edFiles);
            string folder = _edListFolder;
            var counts = await Task.Run(() =>
            {
                var res = new List<int>();
                // Los coches de cada .con ya están leídos (caché de contenido): contarlos no cuesta I/O.
                foreach (var f in files) res.Add(ConsistCarCount(f));
                return res;
            });
            if (_edListFolder != folder || counts.Count != _edFiles.Count) return;   // la lista cambió mientras tanto
            LoadLog("editor: poniendo el nº de coches…");
            // Se cambia solo esa celda de cada fila: la lista no se rehace, así que no parpadea ni
            // pierde el desplazamiento ni la selección mientras el usuario ya está trabajando. Con el
            // repintado parado mientras tanto: miles de filas repintadas una a una eran casi un segundo.
            _edList.BeginUpdate();
            try
            {
                for (int i = 0; i < _edFiles.Count; i++)
                    _edList.SetCell(i, 1, counts[i] > 0 ? counts[i].ToString() : "—", applyWidths: false);
            }
            finally { _edList.EndUpdate(); }
            _edList.RefreshWidths();
            LoadLog("editor: nº de coches puesto");
        }

        void DeleteConsist()
        {
            int i = _edList?.SelectedRow ?? -1;
            if (i < 0 || i >= _edFiles.Count) { Msg(_edMsg, Tr("Elige una composición de la lista."), true); return; }
            string file = _edFiles[i];
            if (MessageBox.Show(this, string.Format(Tr("¿Eliminar la composición «{0}»?\n\nSe envía a la papelera de Windows."), Path.GetFileName(file)),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                // A la papelera de Windows (recuperable desde ahí), sin dejar copias sueltas.
                Native.RecycleFile(file);
                ClearContentCaches();
                if (File.Exists(file + ".bak")) Native.RecycleFile(file + ".bak");   // copia de versiones antiguas de SelectOR
            }
            catch (Exception e) { Msg(_edMsg, Tr("No se pudo eliminar: ") + e.Message, true); return; }
            _edDoc = null; _edOriginal = null;
            SetEditorEnabled(false);
            ReloadConsistsAfterEdit(changed: new[] { file });
            LoadEditorConsists();
            Msg(_edMsg, string.Format(Tr("Composición eliminada: {0}"), Path.GetFileName(file)), false);
        }

        // Exploración → «Editar composición»: el Editor con ese .con ya abierto (si había cambios sin guardar en
        // otra composición, el Editor pregunta como siempre al cambiar de tren).
        void EditConsistFromExplore(TrainItem c)
        {
            if (c?.FilePath == null || !File.Exists(c.FilePath)) return;
            string path = c.FilePath;
            ShowPage(5);
            int i = _edFiles.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && _edList != null)
            {
                _edList.SelectRow(i);
                if (_edDoc == null || !string.Equals(_edDoc.Path, path, StringComparison.OrdinalIgnoreCase)) OnEditorConsistSelected();
                try { _edList.Focus(); } catch { }
            }
            else LoadEditorConsists(path);   // la lista aún no tenía ese archivo: se rehace con él elegido
        }

        // Exploración → «Eliminar composición…»: a la papelera de Windows, como desde el Editor. Con varias
        // marcadas (Ctrl+clic), todas a la vez. La lista se queda donde estaba, con el tren siguiente elegido.
        static Action<string> _recycleFile = Native.RecycleFile;   // a la papelera (las pruebas lo sustituyen)

        void DeleteConsistsFromExplore(List<TrainItem> sel, bool confirm = true)
        {
            var list = sel.Where(c => c?.FilePath != null && File.Exists(c.FilePath)).ToList();
            if (list.Count == 0) return;
            string msg = list.Count == 1
                ? string.Format(Tr("¿Eliminar la composición «{0}»?\n\nSe envía a la papelera de Windows."), Path.GetFileName(list[0].FilePath))
                : string.Format(Tr("¿Eliminar {0} composiciones?"), list.Count) + "\n\n"
                  + string.Join("\n", list.Take(12).Select(c => "• " + Path.GetFileName(c.FilePath)))
                  + (list.Count > 12 ? "\n" + string.Format(Tr("… y {0} más"), list.Count - 12) : "")
                  + "\n\n" + Tr("Se envían a la papelera de Windows.");
            if (confirm && MessageBox.Show(this, msg, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            // El que quedará elegido: el primero que no se borra a partir del primero borrado (o el anterior).
            var gone = new HashSet<object>(list);
            int first = list.Select(c => _lstConsists.Items.IndexOf(c)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            TrainItem next = null;
            if (first >= 0)
            {
                for (int i = first; i < _lstConsists.Items.Count && next == null; i++) if (!gone.Contains(_lstConsists.Items[i])) next = _lstConsists.Items[i] as TrainItem;
                for (int i = first - 1; i >= 0 && next == null; i--) if (!gone.Contains(_lstConsists.Items[i])) next = _lstConsists.Items[i] as TrainItem;
            }
            int top = _lstConsists.TopIndex;

            var fallos = new List<string>();
            foreach (var c in list)
            {
                string file = c.FilePath;
                try
                {
                    _recycleFile(file);
                    if (File.Exists(file + ".bak")) _recycleFile(file + ".bak");
                }
                catch (Exception e) { fallos.Add(Path.GetFileName(file) + ": " + e.Message); continue; }
                _prefs.FavoriteTrains.Remove(file);
                if (_edDoc != null && string.Equals(_edDoc.Path, file, StringComparison.OrdinalIgnoreCase)) { _edDoc = null; _edOriginal = null; SetEditorEnabled(false); }
            }
            ClearContentCaches();
            _lstConsists.ClearMarks();
            if (next != null) _prefs.LastConsist = next.FilePath;
            if (fallos.Count > 0) Warn(Tr("No se pudo eliminar: ") + string.Join("\n", fallos));
            // Exploración, Horarios, Compra… sin esos trenes; la lista vuelve a la misma altura
            ReloadConsistsAfterEdit(() => { try { _lstConsists.TopIndex = Math.Min(top, Math.Max(0, _lstConsists.Items.Count - 1)); } catch { } },
                                    list.Select(c => c.FilePath));
            LoadEditorConsists();
        }

        static string SafeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        // ============================ Vistas 2D y 3D ============================
        void OnEditorCarSelected()
        {
            int i = _edCars?.SelectedRow ?? -1;
            Show3DCar(i);
            Highlight2D();
            ShowCarCapacity(i);
        }

        // ============================ Plazas del coche (PassengerCapacity) ============================
        // Se editan en el ARCHIVO del vehículo (.eng/.wag), no en el .con: afecta a todos los trenes
        // que lleven ese coche. Es el dato con el que SelectOR sabe si un tren lleva viajeros.
        void ShowCarCapacity(int index)
        {
            if (_edCap == null) return;
            _edCapPath = null;
            if (_edDoc == null || index < 0 || index >= _edDoc.Cars.Count)
            {
                _edCap.Box.Text = ""; _edCap.Enabled = false;
                if (_edCapApply != null) _edCapApply.Enabled = false;
                if (_edCapClear != null) _edCapClear.Enabled = false;
                _edCapLbl.Text = Tr("Plazas de viajeros (PassengerCapacity)");
                return;
            }
            var c = _edDoc.Cars[index];
            _edCapPath = ResolveCarFile(c.Name, c.Folder);
            bool ok = _edCapPath != null;
            _edCap.Enabled = ok;
            if (_edCapApply != null) _edCapApply.Enabled = ok;
            if (_edCapClear != null) _edCapClear.Enabled = ok;
            if (!ok) { _edCap.Box.Text = ""; _edCapLbl.Text = Tr("Plazas de viajeros (PassengerCapacity)") + "  ·  " + Tr("archivo no encontrado"); return; }
            double? cap = null;
            try { cap = StockFile.GetCapacity(_edCapPath); } catch { }
            _edCap.Box.Text = cap.HasValue ? cap.Value.ToString("0", EsEs) : "";
            _edCapLbl.Text = Tr("Plazas de viajeros (PassengerCapacity)") + "  ·  "
                           + (cap.HasValue ? string.Format(Tr("ahora: {0}"), cap.Value.ToString("0", EsEs)) : Tr("sin plazas (mercancías)"));
        }

        void ApplyCarCapacity(bool remove)
        {
            if (_edCapPath == null) { Msg(_edMsg, Tr("Elige un coche del tren."), true); return; }
            double? value = null;
            if (!remove)
            {
                double v = ParseNum(_edCap.Box.Text);
                if (v < 1 || v > 5000) { Msg(_edMsg, Tr("Escribe un número de plazas entre 1 y 5000 (o pulsa Quitar)."), true); return; }
                value = v;
            }
            string file = System.IO.Path.GetFileName(_edCapPath);
            string q = remove
                ? string.Format(Tr("¿Quitar las plazas de «{0}»? Dejará de contar como coche de viajeros en TODOS los trenes que lo lleven."), file)
                : string.Format(Tr("¿Poner {0} plazas en «{1}»? Se aplica a TODOS los trenes que lleven ese coche."), ParseNum(_edCap.Box.Text).ToString("0", EsEs), file);
            if (MessageBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string err = StockFile.SetCapacity(_edCapPath, value);
            ClearContentCaches();   // han cambiado las plazas del vehículo
            if (err != null) { Msg(_edMsg, Tr("No se pudo guardar las plazas: ") + err, true); return; }
            Msg(_edMsg, remove
                ? string.Format(Tr("Plazas quitadas de {0}."), file)
                : string.Format(Tr("Plazas guardadas en {0}."), file), false);
            ShowCarCapacity(_edCars?.SelectedRow ?? -1);
            UpdateEditorStats();   // PLAZAS y TIPO cambian al momento
            RefreshTrainsUsing(_edCapPath);   // Exploración: plazas, tipo y filtro Viajeros/Mercancías de esos trenes
        }

        // 3D del coche elegido (se puede girar arrastrando; doble clic vuelve a la vista inicial).
        string _ed3DSource;   // qué enseña la vista 3D: "car<n>" (coche del tren) o "stock<n>" (material disponible)
        void Show3DCar(int index)
        {
            if (_edPreview3D == null) return;
            string path = null; bool flip = false; string name = null;
            if (_edDoc != null && index >= 0 && index < _edDoc.Cars.Count)
            {
                var c = _edDoc.Cars[index];
                path = ResolveCarFile(c.Name, c.Folder);
                flip = c.Flip; name = c.Name;
            }
            _ed3DSource = "car" + index;
            Show3DFile(path, flip, name);
        }

        // Material disponible: el vehículo elegido en la tabla de la derecha, en su sentido normal.
        void Show3DStock(int index)
        {
            if (_edPreview3D == null || index < 0 || index >= _edStockAll.Count) return;   // sin fila elegida: se deja lo que hay
            if (_ed3DSource == "stock" + index) return;
            var it = _edStockAll[index];
            _ed3DSource = "stock" + index;
            Show3DFile(File.Exists(it.path) ? it.path : null, false, it.name);
        }

        void Show3DFile(string path, bool flip, string name)
        {
            if (_edPreview3D == null) return;
            _ed3DTitle.Text = Tr("VISTA 3D DEL COCHE") + (name != null ? "  ·  " + name : "");
            _edGeomPath = path; _edGeomFlip = flip; _edGeom = null;
            _edPreview3D.Image = null; _edPreview3D.Rotatable = false;
            _edPreview3D.Caption = name ?? "";
            // Sin coche elegido no se está generando nada: se dice eso, no «Generando vista 3D…».
            _edPreview3D.EmptyText = path == null ? Tr("Elige un coche del tren") : null;
            _edPreview3D.Invalidate();
            if (path == null) return;
            if (_geomCache.TryGetValue(path, out var cached) && cached != null)
            {
                _edGeom = cached; _edPreview3D.Rotatable = true; Render3DLive();
                return;
            }
            string want = path;
            Task.Run(() =>
            {
                ShapeGeom geom = null;
                try { geom = ShapeRenderer.BuildGeometry(want); } catch { }
                try { ShapeRenderer.PrefetchTextures(geom); } catch { }   // texturas listas antes de renderizar (si no, se descodifican en el hilo de la interfaz)
                if (geom != null) lock (_geomCache) _geomCache[want] = geom;
                if (!IsHandleCreated) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (_edGeomPath != want || geom == null) return;
                        _edGeom = geom; _edPreview3D.Rotatable = true; Render3DLive();
                    }));
                }
                catch { }
            });
        }

        void Render3DLive()
        {
            if (_edGeom == null || _edPreview3D == null || _edPreview3D.Width < 40 || _edPreview3D.Height < 40) return;
            int w = Math.Min(1400, Math.Max(128, (_edPreview3D.Width - 8) * 2));
            int h = Math.Min(820, Math.Max(96, (_edPreview3D.Height - 36) * 2));
            var bmp = ShapeRenderer.Render(_edGeom, w, h, _edYaw, _edPitch, 1, _edGeomFlip, _edPreview3D.CamDistance());
            if (bmp != null) _edPreview3D.Image = bmp;
        }

        // La tira 2D se rehace tras un respiro (añadir/quitar/mover varios coches seguidos no la recalcula cada vez).
        void Queue2DRender()
        {
            if (_ed2DPic == null) return;
            if (_ed2DTimer == null)
            {
                _ed2DTimer = new System.Windows.Forms.Timer { Interval = 350 };
                _ed2DTimer.Tick += (s, e) => { _ed2DTimer.Stop(); Render2D(); };
            }
            _ed2DTimer.Stop(); _ed2DTimer.Start();
        }

        void Render2D()
        {
            if (_ed2DPic == null) return;
            var doc = _edDoc;
            if (doc == null || doc.Cars.Count == 0)
            {
                _ed2DSlots.Clear();
                var old0 = _ed2DPic.Image; _ed2DPic.Image = null; old0?.Dispose();
                _ed2DInfo.Text = Tr("COMPOSICIÓN 2D");
                ShowConsistStats(null);
                return;
            }
            // Rutas de los modelos (en el hilo de UI: ResolveCarFile usa la carpeta activa).
            var models = new List<(string path, bool flip, string name, bool isEngine)>();
            foreach (var c in doc.Cars) models.Add((ResolveCarFile(c.Name, c.Folder), c.Flip, c.Name, c.IsEngine));
            int token = ++_ed2DToken;
            float ppm = Ed2DPpm();
            _ed2DInfo.Text = Tr("COMPOSICIÓN 2D") + "  ·  " + Tr("dibujando…");
            Task.Run(() =>
            {
                var slots = new List<(Bitmap bmp, bool missing, string name, ShapeRenderer.SideExtent ext)>();
                double meters = 0;
                var st = ComputeConsistStats(models);
                foreach (var (path, flip, name, isEngine) in models)
                {
                    ShapeGeom geom = null;
                    if (path != null)
                    {
                        lock (_geomCache) _geomCache.TryGetValue(path, out geom);
                        if (geom == null)
                        {
                            try { geom = ShapeRenderer.BuildGeometry(path); } catch { }
                            try { ShapeRenderer.PrefetchTextures(geom); } catch { }   // texturas listas antes de renderizar (si no, se descodifican en el hilo de la interfaz)
                            if (geom != null) lock (_geomCache) _geomCache[path] = geom;
                        }
                    }
                    if (geom == null) { slots.Add((null, true, name, new ShapeRenderer.SideExtent(40, 0, 39, 0, 39))); continue; }
                    // Misma regla de orientación que la ventana de composición (cabeza a la izquierda).
                    Bitmap bmp = null;
                    try { bmp = ShapeRenderer.RenderSide(geom, ppm, Ed2DWorldH, flip ^ true); } catch { }
                    if (bmp == null) { slots.Add((null, true, name, new ShapeRenderer.SideExtent(40, 0, 39, 0, 39))); continue; }
                    // Acoplado: topes con topes (ver ShapeRenderer.CoupledLayout).
                    meters += geom.Max.Z - geom.Min.Z;
                    slots.Add((bmp, false, name, ShapeRenderer.MeasureSide(bmp)));
                }
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)(() => Compose2D(token, slots, meters, st))); } catch { }
            });
        }

        void Compose2D(int token, List<(Bitmap bmp, bool missing, string name, ShapeRenderer.SideExtent ext)> slots, double meters, ConsistStats st)
        {
            if (token != _ed2DToken || _ed2DPic == null) return;
            const int missingW = 40;
            int carH = (int)(Ed2DWorldH * Ed2DPpm());
            int h = carH + Ed2DLabelH;
            int missing = 0;
            var lay = ShapeRenderer.CoupledLayout(slots.Select(s => s.ext).ToList(), Ed2DPpm(), 0);
            int total = lay.total;
            var composite = new Bitmap(total, h);
            _ed2DSlots.Clear();
            using (var g = Graphics.FromImage(composite))
            {
                g.Clear(Color.Transparent);
                using (var pen = new Pen(Color.FromArgb(90, Theme.Subtle), 1.5f)) g.DrawLine(pen, 0, h - 2, total, h - 2);
                using var fNum = Theme.Font(8f, FontStyle.Bold);
                using var fName = Theme.Font(8f);
                int idx = 0;
                // Primero todos los vehículos (se solapan un poco, como en la vía) y después los rótulos encima.
                for (int i = 0; i < slots.Count; i++)
                    if (slots[i].bmp != null) g.DrawImage(slots[i].bmp, lay.drawX[i], Ed2DLabelH);
                for (int i = 0; i < slots.Count; i++)
                {
                    var s = slots[i];
                    int x = lay.slotL[i], w = Math.Max(1, lay.slotR[i] - lay.slotL[i]);
                    if (s.bmp != null) { }
                    else
                    {
                        missing++;
                        using var br = new SolidBrush(Color.FromArgb(60, 229, 115, 115));
                        g.FillRectangle(br, x, Ed2DLabelH + carH / 3, w, carH / 3);
                    }
                    // Nº de orden + nombre del coche, CENTRADOS sobre cada vehículo.
                    idx++;
                    const int numGap = 12;   // separación entre el número y el nombre
                    string num = idx.ToString();
                    string cname = s.name ?? "";
                    var numSz = TextRenderer.MeasureText(g, num, fNum, Size.Empty, TextFormatFlags.NoPadding);
                    var nameSz = TextRenderer.MeasureText(g, cname, fName, Size.Empty, TextFormatFlags.NoPadding);
                    int room = Math.Max(0, w - 6);
                    int nameW = Math.Min(nameSz.Width, Math.Max(0, room - numSz.Width - numGap));
                    int lblW = numSz.Width + (nameW > 14 ? numGap + nameW : 0);
                    int lblX = x + Math.Max(3, (w - lblW) / 2);
                    TextRenderer.DrawText(g, num, fNum, new Rectangle(lblX, 2, numSz.Width + 2, Ed2DLabelH - 4),
                        Theme.AccentHi, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    if (nameW > 14 && cname.Length > 0)
                        TextRenderer.DrawText(g, cname, fName, new Rectangle(lblX + numSz.Width + numGap, 2, nameW, Ed2DLabelH - 4),
                            Blend(Theme.Text, Theme.Subtle, 0.25f),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    _ed2DSlots.Add((x, x + w));
                    s.bmp?.Dispose();
                }
            }
            var old = _ed2DPic.Image;
            _ed2DPic.Image = composite;
            old?.Dispose();
            string info = Tr("COMPOSICIÓN 2D") + "  ·  " + slots.Count + " " + Tr("coches") + "  ·  " + meters.ToString("N0", EsEs) + " m";
            if (missing > 0) info += "  ·  " + string.Format(Tr("{0} sin modelo"), missing);
            _ed2DInfo.Text = info;
            ShowConsistStats(st);
            Highlight2D();
        }

        // Tarjetas COCHES, LONGITUD, MASA…: se recalculan EN CUANTO cambia algo (coches, orden, plazas),
        // sin esperar al dibujo 2D, que tiene que construir la geometría de cada coche y tarda.
        int _edStatsToken;
        void UpdateEditorStats()
        {
            if (_stCars == null) return;
            var doc = _edDoc;
            if (doc == null || doc.Cars.Count == 0) { _edStatsToken++; ShowConsistStats(null); return; }
            var models = new List<(string path, bool flip, string name, bool isEngine)>();
            foreach (var c in doc.Cars) models.Add((ResolveCarFile(c.Name, c.Folder), c.Flip, c.Name, c.IsEngine));
            int token = ++_edStatsToken;
            Task.Run(() =>
            {
                var st = ComputeConsistStats(models);
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)(() => { if (token == _edStatsToken) ShowConsistStats(st); })); } catch { }
            });
        }

        // Datos del tren sumando sus coches (masa, longitud, freno, plazas y, de las motrices, potencia y
        // velocidad). Todo sale de la caché de contenido, que se vacía al cambiar las plazas de un coche.
        ConsistStats ComputeConsistStats(List<(string path, bool flip, string name, bool isEngine)> models)
        {
            var st = new ConsistStats();
            foreach (var (path, _, _, isEngine) in models)
            {
                st.Cars++;
                if (isEngine) st.Engines++;
                string declared = DeclaredVehicleType(path);
                if (declared == "carriage" || declared == "passenger") st.Pax = true;
                else if (declared == "freight") st.Freight = true;
                var veh = VehicleStats(path);
                if (veh == null) continue;
                st.MassT += veh.MassT;
                st.BrakeKn += veh.BrakeKn;
                st.Capacity += veh.Capacity;
                if (veh.Length > 0) st.LengthM += veh.Length;
                if (isEngine)
                {
                    st.PowerKw += veh.PowerKw;
                    double kmh = veh.SpeedKmh;
                    if (kmh > 0) st.MaxKmh = st.MaxKmh <= 0 ? kmh : Math.Min(st.MaxKmh, kmh);
                }
            }
            st.Kind = st.Pax ? Tr("Viajeros") : st.Freight ? Tr("Mercancías") : "";
            return st;
        }

        // Datos sumados de toda la composición.
        sealed class ConsistStats
        {
            public int Cars, Engines;
            public double MassT, LengthM, BrakeKn, Capacity, PowerKw, MaxKmh;
            public bool Pax;         // algún coche declara Type ( Carriage/Passenger )
            public bool Freight;     // algún coche declara Type ( Freight )
            public string Kind = ""; // resumen del tipo declarado por los coches
        }

        void ShowConsistStats(ConsistStats st)
        {
            if (_stCars == null) return;
            if (st == null || st.Cars == 0)
            {
                foreach (var l in new[] { _stCars, _stLen, _stMass, _stPower, _stSpeed, _stBrake, _stCap, _stType })
                    if (l != null) { l.Text = "—"; l.ForeColor = Theme.Text; }
                return;
            }
            _stCars.Text = st.Cars.ToString("N0", EsEs) + (st.Engines > 0 ? "  (" + st.Engines + " " + Tr(st.Engines == 1 ? "motriz" : "motrices") + ")" : "");
            _stLen.Text = st.LengthM > 0 ? st.LengthM.ToString("N0", EsEs) + " m" : "—";
            _stMass.Text = st.MassT > 0 ? st.MassT.ToString("N0", EsEs) + " t" : "—";
            _stBrake.Text = st.BrakeKn > 0 ? st.BrakeKn.ToString("N0", EsEs) + " kN" : "—";
            _stPower.Text = st.PowerKw > 0 ? st.PowerKw.ToString("N0", EsEs) + " kW" : "—";
            _stSpeed.Text = st.MaxKmh > 0 ? st.MaxKmh.ToString("N0", EsEs) + " km/h" : "—";
            _stCap.Text = st.Capacity > 0 ? st.Capacity.ToString("N0", EsEs) : "—";
            // TIPO del tren según lo que declaran sus coches (y, si no lo declaran, según las plazas).
            bool pax = st.Pax || st.Capacity > 0;
            _stType.Text = st.Kind.Length > 0 ? st.Kind : (pax ? Tr("Viajeros") : Tr("Mercancías"));
            _stType.ForeColor = pax ? Theme.Accent : ColOrange;
        }

        // Marca en la tira 2D el coche seleccionado (y lo deja a la vista).
        void Highlight2D()
        {
            if (_ed2DPic?.Image == null || _ed2DHost == null) return;
            int i = _edCars?.SelectedRow ?? -1;
            _ed2DPic.Invalidate();
            if (i < 0 || i >= _ed2DSlots.Count) return;
            var (x0, x1) = _ed2DSlots[i];
            int viewW = _ed2DHost.ClientSize.Width;
            if (viewW > 40)
            {
                int target = Math.Max(0, (x0 + x1) / 2 - viewW / 2);
                try { _ed2DHost.AutoScrollPosition = new Point(target, 0); } catch { }
            }
        }

        void PickCarFrom2D(int x)
        {
            for (int i = 0; i < _ed2DSlots.Count; i++)
                if (x >= _ed2DSlots[i].x0 && x <= _ed2DSlots[i].x1) { _edCars.SelectRow(i); return; }
        }

        // Tras crear/editar/borrar: SelectOR vuelve a leer los trenes de la carpeta (Exploración,
        // Horarios, Empresas…), sin recargar rutas ni actividades.
        // Ha cambiado un vehículo (.eng/.wag): se ponen al día TODOS los trenes que lo llevan.
        void RefreshTrainsUsing(string vehPath)
        {
            if (string.IsNullOrEmpty(vehPath)) return;
            string vehName = Path.GetFileNameWithoutExtension(vehPath), vehFolder = Path.GetFileName(Path.GetDirectoryName(vehPath) ?? "");
            var all = _consistsAll?.ToList() ?? new List<TrainItem>();
            Task.Run(() =>
            {
                var afectados = all.Where(c => c?.FilePath != null && ConsistCarRefs(c.FilePath).Any(r =>
                        string.Equals(r.name, vehName, StringComparison.OrdinalIgnoreCase) && string.Equals(r.folder, vehFolder, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => c.FilePath).ToList();
                try { BeginInvoke((Action)(() => ReloadConsistsAfterEdit(changed: afectados))); } catch { }
            });
        }

        // El contenido de estos trenes ha cambiado (Editor): fuera sus datos (tipo, masa, longitud, plazas…), su
        // composición 2D y su línea en la caché de disco; se vuelven a calcular. Antes se quedaban los de antes
        // (iban por la ruta del archivo) hasta reiniciar SelectOR.
        void InvalidateTrains(IEnumerable<string> conPaths)
        {
            var set = new HashSet<string>((conPaths ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
            if (set.Count == 0) return;
            lock (_trainSpecs) foreach (var p in set) _trainSpecs.Remove(p);
            _lstConsists?.ForgetSpecs(set);   // la tarjeta guarda los suyos (coches, plazas, viajeros/mercancías)
            foreach (var p in set)
                if (_stripMem.TryGetValue(p, out var b)) { _stripMem.Remove(p); if (!ReferenceEquals(b, _exStrip?.Image)) b.Dispose(); }
            var lower = new HashSet<string>(set.Select(p => p.ToLowerInvariant()));
            bool quitado = false;
            lock (_classDisk)
                foreach (var k in _classDisk.Keys.ToList())
                {
                    int bar = k.LastIndexOf('|');
                    if (lower.Contains(bar > 0 ? k.Substring(0, bar) : k)) { _classDisk.Remove(k); quitado = true; }
                }
            if (quitado) Task.Run(SaveClassDisk);
        }

        void ReloadConsistsAfterEdit(Action after = null, IEnumerable<string> changed = null)
        {
            var folder = _curFolder;
            if (folder == null) return;
            var cambiados = (changed ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrEmpty(p)).ToList();
            InvalidateTrains(cambiados);
            lock (_consistCache) _consistCache.Remove(folder.Path);
            Task.Run(() =>
            {
                FastConsists.ClearCache();   // el editor ha tocado archivos del contenido
                var consists = FastConsists.Load(folder.Path);
                if (consists.Count == 0)
                    consists = SafeList(() => ORTS.Menu.Consist.GetConsists(folder).Select(FromOrConsist).OrderBy(c => c.Name).ToList());
                lock (_consistCache) _consistCache[folder.Path] = consists;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _consistsAll = consists;
                        RefreshConsistList();
                        after?.Invoke();
                        RebuildCompanyEngs();
                        UpdateStatus();
                        if (cambiados.Count > 0)
                        {
                            StartTrainClassification(_consistsAll);   // recalcula los que han cambiado (el resto ya se sabe)
                            // La ficha y la composición 2D del tren elegido, si es uno de ellos
                            if (_lstConsists?.SelectedItem is TrainItem sel && cambiados.Any(p => string.Equals(p, sel.FilePath, StringComparison.OrdinalIgnoreCase)))
                                UpdateExploreSpecs(sel);
                        }
                    }));
                }
                catch { }
            });
        }
    }
}
