using System;
using System.IO;
using System.Windows;
using System.Windows.Resources;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace IWar3Key.Shell.Interop
{
    internal static class AppIcon
    {
        private const string ResourceUri = "pack://application:,,,/assets/iwar3key.ico";

        public static Drawing.Icon LoadSmall()
        {
            return Load(Forms.SystemInformation.SmallIconSize);
        }

        public static Drawing.Icon LoadLarge()
        {
            return Load(new Drawing.Size(32, 32));
        }

        private static Drawing.Icon Load(Drawing.Size size)
        {
            try
            {
                StreamResourceInfo resource = Application.GetResourceStream(new Uri(ResourceUri, UriKind.Absolute));
                if (resource != null)
                {
                    using (Stream stream = resource.Stream)
                    {
                        return new Drawing.Icon(stream, size);
                    }
                }
            }
            catch
            {
                // Fall through to the executable icon.
            }

            try
            {
                string exePath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    Drawing.Icon extracted = Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (extracted != null)
                    {
                        return extracted;
                    }
                }
            }
            catch
            {
                // Fall through to the system icon.
            }

            return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        }
    }
}
