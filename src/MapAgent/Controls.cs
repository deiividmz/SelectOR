// Mandos de SelectOR (Ajustes de SelectOR → Mandos) dentro de Open Rails, por la conexión con SelectOR (LocalLink.cs),
// en la vuelta del simulador:
//  · «CAB TIPO valor»: una palanca → el «dispositivo web» de Open Rails (UserInput.WebDeviceState.CabControls), el
//    mismo que llena su servidor web con POST /API/CABCONTROLS; la locomotora lo aplica como siempre (regulador,
//    frenos, inversor… MSTSLocomotiveViewer.HandleUserInput).
//  · «BTN Orden 1|0»: un botón → un botón de dispositivo externo registrado en el del RailDriver (UserInput.RDState,
//    que existe aunque no haya RailDriver): Open Rails lo atiende como una tecla (UserInput.IsPressed/IsReleased), aunque
//    no sea la ventana activa. La orden va por su NOMBRE («A|B»: la primera que exista en esta versión).
//  · «CRUISE fracción máx»: la palanca del control de crucero → la velocidad seleccionada de su CruiseControl (lo mismo
//    que hace Open Rails con ORTS_SELECTED_SPEED_SELECTOR, pero sin romperse si la locomotora no lo tiene): de 0 a la
//    máxima del servicio (máx, km/h) o, con 0, a la de la locomotora; de 5 en 5 km/h.
// Todo por reflexión (lo de RunActivity no se compila aquí): si algo no está, esa orden no hace nada.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SelectOR.MapAgent
{
    static class Controls
    {
        const BindingFlags BF = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static bool _init, _ok;
        static FieldInfo _rdState, _webState, _cabControls, _commands;
        static Type _cvct, _cabCtl, _btn, _cmdType, _keyType;
        static PropertyInfo _cabValue, _btnDown;
        static MethodInfo _register;
        static readonly Dictionary<string, object> _keys = new Dictionary<string, object>();       // tipo → clave de CabControls
        static readonly Dictionary<string, object> _buttons = new Dictionary<string, object>();    // orden → nuestro botón
        static readonly HashSet<string> _missing = new HashSet<string>();

        static bool Init()
        {
            if (_init) return _ok;
            _init = true;
            try
            {
                Type ui = null;
                foreach (var a in new[] { System.Reflection.Assembly.GetEntryAssembly() }.Concat(AppDomain.CurrentDomain.GetAssemblies()))
                    if (a != null && (ui = a.GetType("Orts.Viewer3D.UserInput")) != null) break;
                if (ui == null) { Agent.Log("mandos: no está UserInput"); return false; }
                _rdState = ui.GetField("RDState", BF); _webState = ui.GetField("WebDeviceState", BF);
                var asm = ui.Assembly;
                var eds = asm.GetType("Orts.Viewer3D.ExternalDeviceState");
                _cabCtl = asm.GetType("Orts.Viewer3D.ExternalDeviceCabControl"); _btn = asm.GetType("Orts.Viewer3D.ExternalDeviceButton");
                _cvct = typeof(Orts.Formats.Msts.TrackNode).Assembly.GetType("Orts.Formats.Msts.CabViewControlType");
                if (eds == null || _cabCtl == null || _btn == null || _cvct == null || _webState == null || _rdState == null) { Agent.Log("mandos: falta el dispositivo externo de Open Rails"); return false; }
                _cabControls = eds.GetField("CabControls", BF); _commands = eds.GetField("Commands", BF);
                _register = eds.GetMethod("RegisterCommand", BF);
                _cabValue = _cabCtl.GetProperty("Value", BF); _btnDown = _btn.GetProperty("IsDown", BF);
                _cmdType = _register?.GetParameters()[0].ParameterType;
                _keyType = _cabControls?.FieldType.GetGenericArguments()[0];
                _ok = _cabControls != null && _register != null && _cabValue != null && _btnDown != null && _cmdType != null && _keyType != null;
                Agent.Log(_ok ? "mandos: listos (palancas por el dispositivo web, botones por el del RailDriver)" : "mandos: Open Rails sin lo necesario");
            }
            catch (Exception ex) { Agent.Log("mandos: " + ex.GetType().Name + " " + ex.Message); _ok = false; }
            return _ok;
        }

        // (en la vuelta del simulador) true = era de mandos.
        public static bool Do(string cmd, object sim)
        {
            if (cmd.StartsWith("CRUISE ", StringComparison.Ordinal)) { Cruise(cmd, sim); return true; }
            bool cab = cmd.StartsWith("CAB ", StringComparison.Ordinal), btn = cmd.StartsWith("BTN ", StringComparison.Ordinal);
            if (!cab && !btn) return false;
            if (!Init()) return true;
            var t = cmd.Split(' ');
            if (t.Length < 3) return true;
            try
            {
                if (cab)
                {
                    if (!float.TryParse(t[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) return true;
                    var ws = _webState.GetValue(null); if (ws == null) return true;
                    var dict = (IDictionary)_cabControls.GetValue(ws);
                    if (!_keys.TryGetValue(t[1], out var key))
                    {
                        var cv = Activator.CreateInstance(_cvct, t[1]);
                        _keys[t[1]] = key = Activator.CreateInstance(_keyType, cv, 0);
                    }
                    object ctl = dict.Contains(key) ? dict[key] : null;
                    if (ctl == null)
                    {
                        // (como el servidor web: un diccionario nuevo con la entrada añadida)
                        ctl = Activator.CreateInstance(_cabCtl);
                        var copy = (IDictionary)Activator.CreateInstance(dict.GetType(), dict);
                        copy[key] = ctl;
                        _cabControls.SetValue(ws, copy);
                    }
                    _cabValue.SetValue(ctl, v);
                }
                else
                {
                    var rd = _rdState.GetValue(null); if (rd == null) return true;
                    string name = t[1];
                    if (!_buttons.TryGetValue(name, out var b))
                    {
                        object uc = null;
                        foreach (var n in name.Split('|')) try { uc = Enum.Parse(_cmdType, n); break; } catch { }
                        if (uc == null) { if (_missing.Add(name)) Agent.Log("mandos: esta versión de Open Rails no tiene la orden " + name); return true; }
                        b = Activator.CreateInstance(_btn);
                        _register.Invoke(rd, new[] { uc, b });
                        _buttons[name] = b;
                    }
                    _btnDown.SetValue(b, t[2] == "1");
                }
            }
            catch (Exception ex) { Agent.Log("mandos: " + cmd + ": " + ex.GetType().Name + " " + ex.Message); }
            return true;
        }

        static bool _noCruiseTold; static double _cruiseSent = -1;
        static object Member(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var f = t.GetField(name, BF); if (f != null) return f.GetValue(o);
            var p = t.GetProperty(name, BF); return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(o) : null;
        }
        static bool SetMember(object o, string name, object v)
        {
            var t = o.GetType();
            var f = t.GetField(name, BF); if (f != null) { f.SetValue(o, Convert.ChangeType(v, f.FieldType)); return true; }
            var p = t.GetProperty(name, BF); if (p != null && p.CanWrite) { p.SetValue(o, Convert.ChangeType(v, p.PropertyType)); return true; }
            return false;
        }

        static void Cruise(string cmd, object sim)
        {
            try
            {
                var t = cmd.Split(' ');
                if (t.Length < 3 || !double.TryParse(t[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double frac)) return;
                double.TryParse(t[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double maxKmh);
                var loco = Member(sim, "PlayerLocomotive");
                var cc = Member(loco, "CruiseControl");
                if (cc == null)
                {
                    if (!_noCruiseTold) { _noCruiseTold = true; Agent.Log("mandos: esta locomotora no tiene control de crucero"); }
                    return;
                }
                if (maxKmh <= 0)
                {
                    var m = Member(loco, "MaxSpeedMpS");
                    double mps = m == null ? 0 : Convert.ToDouble(m);
                    maxKmh = mps > 0 && mps < 120 ? mps * 3.6 : 160;   // (sin dato, Open Rails pone 1000 m/s)
                }
                double kmh = Math.Round(Math.Max(0, Math.Min(1, frac)) * maxKmh / 5) * 5;
                if (Math.Abs(kmh - _cruiseSent) < 0.1) return;
                if (SetMember(cc, "SelectedSpeedMpS", (float)(kmh / 3.6))) _cruiseSent = kmh;
                else if (!_noCruiseTold) { _noCruiseTold = true; Agent.Log("mandos: el control de crucero de esta versión de Open Rails no tiene SelectedSpeedMpS"); }
            }
            catch (Exception ex) { Agent.Log("mandos: " + cmd + ": " + ex.GetType().Name + " " + ex.Message); }
        }

        // Al soltar la conexión: ningún botón se queda pulsado.
        public static void ReleaseAll()
        {
            try { foreach (var b in _buttons.Values) _btnDown?.SetValue(b, false); } catch { }
        }
    }
}
