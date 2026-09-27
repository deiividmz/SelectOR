// Compatibilidad entre versiones de Open Rails.
//
// SelectOR se compila contra unas DLLs de OR concretas, pero se ejecuta reutilizando las DLLs de la
// instalación del usuario, que pueden ser de OTRA versión (oficial, Testing, "New Year"…). Algunas
// FIRMAS DE CONSTRUCTOR de OR cambian entre versiones (p. ej. ConsistFile pasó de (string,bool) a
// (string)), lo que provoca MissingMethodException en tiempo de ejecución. Para evitarlo, estas clases
// se construyen por REFLEXIÓN eligiendo el constructor que exista en la DLL cargada.

using System;
using System.Reflection;
using Orts.Formats.Msts;

namespace SelectOR
{
    static class OrCompat
    {
        static ConstructorInfo _consist1, _consist2;   // ConsistFile(string) / ConsistFile(string,bool)
        static ConstructorInfo _shape2, _shape1;        // ShapeFile(string,bool) / ShapeFile(string)
        static bool _consistProbed, _shapeProbed;

        /// <summary>Abre un ConsistFile sea cual sea la firma del constructor en la versión de OR cargada.</summary>
        public static ConsistFile OpenConsist(string path)
        {
            if (!_consistProbed)
            {
                var t = typeof(ConsistFile);
                _consist1 = t.GetConstructor(new[] { typeof(string) });
                _consist2 = t.GetConstructor(new[] { typeof(string), typeof(bool) });
                _consistProbed = true;
            }
            if (_consist1 != null) return (ConsistFile)_consist1.Invoke(new object[] { path });
            if (_consist2 != null)
            {
                // usa el valor por defecto del 2º parámetro (así reproduce el comportamiento original)
                var def = _consist2.GetParameters()[1];
                object arg2 = def.HasDefaultValue ? def.DefaultValue : false;
                return (ConsistFile)_consist2.Invoke(new object[] { path, arg2 });
            }
            return (ConsistFile)Activator.CreateInstance(typeof(ConsistFile), path);   // último recurso
        }

        /// <summary>Abre un ShapeFile sea cual sea la firma del constructor en la versión de OR cargada.
        /// Preferimos (string,bool)=true (comportamiento histórico) y, si no existe, (string).</summary>
        public static ShapeFile OpenShapeFile(string path)
        {
            if (!_shapeProbed)
            {
                var t = typeof(ShapeFile);
                _shape2 = t.GetConstructor(new[] { typeof(string), typeof(bool) });
                _shape1 = t.GetConstructor(new[] { typeof(string) });
                _shapeProbed = true;
            }
            if (_shape2 != null) return (ShapeFile)_shape2.Invoke(new object[] { path, true });
            if (_shape1 != null) return (ShapeFile)_shape1.Invoke(new object[] { path });
            return (ShapeFile)Activator.CreateInstance(typeof(ShapeFile), path, true);   // último recurso
        }
    }
}
