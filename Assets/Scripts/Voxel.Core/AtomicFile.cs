using System;
using System.IO;

namespace Voxel.Core
{
    public static class AtomicFile
    {
        // Writes a temporary file first: a failure while writing leaves the old
        // file intact.
        public static void Write(string path, Action<Stream> write)
        {
            string temporaryPath = path + ".tmp";
            using (FileStream stream = File.Create(temporaryPath))
            {
                write(stream);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
    }
}
