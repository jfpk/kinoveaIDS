﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AForge.Video.DirectShow;
using System.Globalization;

namespace Kinovea.Camera.DirectShow
{
    /// <summary>
    /// Reads and writes a list of supported camera properties from/to the device.
    /// </summary>
    public static class CameraPropertyManager
    {
        public static Dictionary<string, CameraProperty> Read(VideoCaptureDevice device)
        {
            Dictionary<string, CameraProperty> properties = new Dictionary<string, CameraProperty>();

            if (device.Logitech_SupportExposureProperty())
                properties.Add("exposure_logitech", ReadLogitechProperty(device));
            else
                properties.Add("exposure", ReadProperty(device, CameraControlProperty.Exposure));

            properties.Add("gain", ReadProperty(device, VideoProcAmpProperty.Gain));
            properties.Add("focus", ReadProperty(device, CameraControlProperty.Focus));
            
            return properties;
        }

        public static void Write(VideoCaptureDevice device, Dictionary<string, CameraProperty> properties)
        {
            if (properties.ContainsKey("exposure_logitech"))
                WriteLogitechProperty(device, properties["exposure_logitech"]);

            if (properties.ContainsKey("exposure"))
                WriteProperty(device, CameraControlProperty.Exposure, properties["exposure"]);

            if (properties.ContainsKey("gain"))
                WriteProperty(device, VideoProcAmpProperty.Gain, properties["gain"]);

            if (properties.ContainsKey("focus"))
                WriteProperty(device, CameraControlProperty.Focus, properties["focus"]);
        }

        public static void Write(VideoCaptureDevice device, CameraProperty property)
        {
            if (!property.Supported)
                return;

            switch (property.Specific)
            {
                case "CameraControl":
                    {
                        CameraControlProperty p = (CameraControlProperty)Enum.Parse(typeof(CameraControlProperty), property.Identifier, true);
                        WriteProperty(device, p, property);
                        break;
                    }
                case "VideoProcAmp":
                    {
                        VideoProcAmpProperty p = (VideoProcAmpProperty)Enum.Parse(typeof(VideoProcAmpProperty), property.Identifier, true);
                        WriteProperty(device, p, property);
                        break;
                    }
                case "Logitech":
                    {
                        WriteLogitechProperty(device, property);
                        break;
                    }
            }
        }

        private static CameraProperty ReadProperty(VideoCaptureDevice device, CameraControlProperty property)
        {
            CameraProperty p = new CameraProperty();
            p.Identifier = property.ToString();
            p.Specific = "CameraControl";
            p.ReadOnly = false;
            p.Type = CameraPropertyType.Integer;
            p.Representation = CameraPropertyRepresentation.LinearSlider;
            p.CanBeAutomatic = true;

            try
            {
                int min;
                int max;
                int step;
                int defaultValue;
                CameraControlFlags flags;
                bool success = device.GetCameraPropertyRange(property, out min, out max, out step, out defaultValue, out flags);

                if (!success)
                {
                    p.Supported = false;
                }
                else
                {
                    p.Supported = true;
                    p.Minimum = min.ToString(CultureInfo.InvariantCulture);
                    p.Maximum = max.ToString(CultureInfo.InvariantCulture);

                    int currentValue;
                    success = device.GetCameraProperty(property, out currentValue, out flags);

                    if (!success)
                    {
                        p.Supported = false;
                    }
                    else
                    {
                        p.CurrentValue = currentValue.ToString(CultureInfo.InvariantCulture);
                        p.Automatic = flags == CameraControlFlags.Auto;
                    }
                }
            }
            catch
            {
                p.Supported = false;
            }

            return p;
        }

        private static CameraProperty ReadProperty(VideoCaptureDevice device, VideoProcAmpProperty property)
        {
            CameraProperty p = new CameraProperty();
            p.Identifier = property.ToString();
            p.Specific = "VideoProcAmp";
            p.ReadOnly = false;
            p.Type = CameraPropertyType.Integer;
            p.Representation = CameraPropertyRepresentation.LinearSlider;
            p.CanBeAutomatic = true;

            try
            {
                int min;
                int max;
                int step;
                int defaultValue;
                VideoProcAmpFlags flags;
                bool success = device.GetVideoPropertyRange(property, out min, out max, out step, out defaultValue, out flags);

                if (!success)
                {
                    p.Supported = false;
                }
                else
                {
                    p.Supported = true;
                    p.Minimum = min.ToString(CultureInfo.InvariantCulture);
                    p.Maximum = max.ToString(CultureInfo.InvariantCulture);

                    int currentValue;
                    success = device.GetVideoProperty(property, out currentValue, out flags);

                    if (!success)
                    {
                        p.Supported = false;
                    }
                    else
                    {
                        p.CurrentValue = currentValue.ToString(CultureInfo.InvariantCulture);
                        p.Automatic = flags == VideoProcAmpFlags.Auto;
                    }
                }
            }
            catch
            {
                p.Supported = false;
            }

            return p;
        }

        private static CameraProperty ReadLogitechProperty(VideoCaptureDevice device)
        {
            // Hardcoded values for min/max according to C920.
            CameraProperty p = new CameraProperty();
            p.Identifier = "Exposure";
            p.Specific = "Logitech";
            p.ReadOnly = false;
            p.Type = CameraPropertyType.Integer;
            p.Representation = CameraPropertyRepresentation.LinearSlider;
            p.CanBeAutomatic = true;

            int min = 1;
            int max = 500;

            p.Supported = true;
            p.Minimum = min.ToString(CultureInfo.InvariantCulture);
            p.Maximum = max.ToString(CultureInfo.InvariantCulture);
            
            int currentValue;
            bool manual;
            bool success = device.Logitech_GetExposure(out currentValue, out manual);

            if (!success)
            {
                p.Supported = false;
            }
            else
            {
                currentValue = Math.Min(max, Math.Max(min, currentValue));
                p.CurrentValue = currentValue.ToString(CultureInfo.InvariantCulture);
                p.Automatic = !manual;
            }

            return p;
        }

        private static void WriteProperty(VideoCaptureDevice device, CameraControlProperty property, CameraProperty value)
        {
            CameraControlFlags flags = value.Automatic ? CameraControlFlags.Auto : CameraControlFlags.Manual;
            int v = int.Parse(value.CurrentValue, CultureInfo.InvariantCulture);
            device.SetCameraProperty(property, v, flags);
        }

        private static void WriteProperty(VideoCaptureDevice device, VideoProcAmpProperty property, CameraProperty value)
        {
            VideoProcAmpFlags flags = value.Automatic ? VideoProcAmpFlags.Auto : VideoProcAmpFlags.Manual;
            int v = int.Parse(value.CurrentValue, CultureInfo.InvariantCulture);
            device.SetVideoProperty(property, v, flags);
        }

        private static void WriteLogitechProperty(VideoCaptureDevice device, CameraProperty value)
        {
            int v = int.Parse(value.CurrentValue, CultureInfo.InvariantCulture);
            device.Logitech_SetExposure(v, !value.Automatic);
        }
    }
}
