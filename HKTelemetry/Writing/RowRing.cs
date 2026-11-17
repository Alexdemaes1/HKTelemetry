using System;
using System.IO;
using System.Text;
using System.Threading;

namespace HKTelemetry.Writing
{
    /// <summary>
    /// Fichero CSV alimentado desde un búfer circular de structs (frames.csv, inputs.csv).
    /// El hilo del juego solo copia un struct a un array ya reservado: cero asignaciones y sin
    /// formatear números. El formateo a texto se hace en el hilo de escritura.
    /// </summary>
    internal abstract class RowSink
    {
        public readonly string Name;
        public readonly string Header;
        public string Path;
        public StreamWriter Writer;
        public long Lines;
        public long Dropped;
        public long Bytes;
        /// <summary>Longitud a la que hay que recortar antes de reabrir (−1 = nada pendiente). Hilo de escritura.</summary>
        public long PendingTruncate = -1;

        /// <summary>Despierta al hilo de escritura (lo asigna TelemetryWriter). Sin asignaciones.</summary>
        public System.Threading.EventWaitHandle Wake;

        protected RowSink(string name, string header)
        {
            Name = name;
            Header = header;
        }

        /// <summary>Hay filas pendientes de escribir (hilo de escritura).</summary>
        public abstract bool HasRows { get; }

        /// <summary>
        /// Pasa las filas pendientes al StreamWriter (hilo de escritura) sin tocar los contadores:
        /// <paramref name="consumed"/> recibe cuántas se sacaron del búfer, también si lanza. Las filas
        /// se consumen aunque la escritura falle; quien llama decide si cuentan como escritas (tras un
        /// Flush correcto) o como descartadas.
        /// </summary>
        public abstract void WriteTo(StreamWriter w, StringBuilder sb, ref int consumed);

        /// <summary>Cierre sin poder escribir: descarta lo pendiente y lo cuenta. Devuelve cuántas filas.</summary>
        public abstract int DiscardPending();
    }

    /// <summary>
    /// Búfer circular de un productor (hilo del juego) y un consumidor (hilo de escritura), sin locks.
    /// Si se llena (el disco se atasca más de lo que cabe), la fila nueva se descarta y se cuenta.
    /// </summary>
    internal sealed class RowRing<T> : RowSink where T : struct
    {
        public delegate void Formatter(ref T row, StringBuilder sb);

        private readonly T[] _buf;
        private readonly int _mask;
        private readonly Formatter _format;
        private int _head;   // siguiente posición a escribir (solo la toca el productor)
        private int _tail;   // siguiente posición a leer (solo la toca el consumidor)

        /// <param name="capacityPow2">capacidad, potencia de 2.</param>
        public RowRing(string name, string header, int capacityPow2, Formatter format) : base(name, header)
        {
            if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("La capacidad debe ser potencia de 2", nameof(capacityPow2));
            _buf = new T[capacityPow2];
            _mask = capacityPow2 - 1;
            _format = format;
        }

        /// <summary>Hilo del juego. Copia la fila al búfer; false si estaba lleno (fila descartada y contada).</summary>
        public bool TryWrite(ref T row)
        {
            int h = _head;
            if (h - Volatile.Read(ref _tail) >= _buf.Length)
            {
                Interlocked.Increment(ref Dropped);
                return false;
            }
            _buf[h & _mask] = row;
            Volatile.Write(ref _head, h + 1); // publica la fila después de copiarla
            // Medio lleno: volcado anticipado en lugar de esperar al ciclo de 500 ms.
            if (h + 1 - Volatile.Read(ref _tail) == (_buf.Length >> 1)) Wake?.Set();
            return true;
        }

        public override bool HasRows => Volatile.Read(ref _head) != _tail;

        public override void WriteTo(StreamWriter w, StringBuilder sb, ref int consumed)
        {
            int h = Volatile.Read(ref _head);
            int start = _tail;
            consumed = h - start;
            try
            {
                for (int t = start; t != h; t++)
                {
                    sb.Length = 0;
                    _format(ref _buf[t & _mask], sb);
                    sb.Append('\n');
                    // StreamWriter.Write(StringBuilder) no existe en .NET 4.7.2: se copia por trozos sin asignar.
                    WriteChars(w, sb);
                }
            }
            finally
            {
                // Libera las posiciones aunque falle: el productor nunca se queda bloqueado.
                // (Las filas solo contienen números y referencias a strings de larga vida: no hace falta limpiarlas.)
                Volatile.Write(ref _tail, h);
            }
        }

        public override int DiscardPending()
        {
            int h = Volatile.Read(ref _head);
            int n = h - _tail;
            Volatile.Write(ref _tail, h);
            if (n > 0) Interlocked.Add(ref Dropped, n);
            return n;
        }

        [ThreadStatic] private static char[] _chunk;

        private static void WriteChars(StreamWriter w, StringBuilder sb)
        {
            var chunk = _chunk ?? (_chunk = new char[1024]);
            int len = sb.Length;
            for (int i = 0; i < len; i += chunk.Length)
            {
                int n = Math.Min(chunk.Length, len - i);
                sb.CopyTo(i, chunk, 0, n);
                w.Write(chunk, 0, n);
            }
        }
    }
}
