using System;
using System.IO;

namespace Voxel.Core
{
    public static class AtomicFile
    {
        // Écrit d'abord un fichier temporaire : une coupure en cours d'écriture laisse l'ancien
        // fichier intact.
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
