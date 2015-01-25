﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Kinovea.Video
{
    public static class BitmapHelper
    {
        [DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
        public static unsafe extern int memcpy(void* dest, void* src, int count);

        /// <summary>
        /// Allocate a new bitmap and copy the passed bitmap into it.
        /// </summary>
        public unsafe static Bitmap Copy(Bitmap src)
        {
            Bitmap dst = new Bitmap(src.Width, src.Height, src.PixelFormat);
            Rectangle rect = new Rectangle(0, 0, src.Width, src.Height);

            BitmapData srcData = src.LockBits(rect, ImageLockMode.ReadOnly, src.PixelFormat);
            BitmapData dstData = dst.LockBits(rect, ImageLockMode.WriteOnly, dst.PixelFormat);

            memcpy(dstData.Scan0.ToPointer(), srcData.Scan0.ToPointer(), srcData.Height * srcData.Stride);

            dst.UnlockBits(dstData);
            src.UnlockBits(srcData);

            return dst;
        }

        /// <summary>
        /// Copy the buffer into the bitmap line by line, with optional vertical flip.
        /// The buffer is assumed RGB24 and the Bitmap must already be allocated.
        /// FIXME: this probably doesn't work well with image size with row padding.
        /// </summary>
        public unsafe static void FillFromRGB24(Bitmap bitmap, Rectangle rect, bool topDown, byte[] buffer)
        {
            BitmapData bmpData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, bitmap.PixelFormat);
            int srcStride = rect.Width * 3;
            int dstStride = bmpData.Stride;

            fixed (byte* pBuffer = buffer)
            {
                byte* src = pBuffer;

                if (topDown)
                {
                    byte* dst = (byte*)bmpData.Scan0.ToPointer();

                    for (int i = 0; i < rect.Height; i++)
                    {
                        memcpy(dst, src, srcStride);
                        src += srcStride;
                        dst += dstStride;
                    }
                }
                else
                {
                    byte* dst = (byte*)bmpData.Scan0.ToPointer() + (dstStride * (rect.Height - 1));

                    for (int i = 0; i < rect.Height; i++)
                    {
                        memcpy(dst, src, srcStride);
                        src += srcStride;
                        dst -= dstStride;
                    }
                }
            }

            bitmap.UnlockBits(bmpData);
        }

        /// <summary>
        /// Copy the buffer into the bitmap.
        /// The buffer is assumed Y800 with no padding and the Bitmap is RGB24 and already allocated.
        /// </summary>
        public unsafe static void FillFromY800(Bitmap bitmap, Rectangle rect, bool topDown, byte[] buffer)
        {
            BitmapData bmpData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, bitmap.PixelFormat);

            int dstOffset = bmpData.Stride - (rect.Width * 3);
            
            fixed (byte* pBuffer = buffer)
            {
                byte* src = pBuffer;
                byte* dst = (byte*)bmpData.Scan0.ToPointer();

                for (int i = 0; i < rect.Height; i++)
                {
                    for (int j = 0; j < rect.Width; j++)
                    {
                        dst[0] = dst[1] = dst[2] = *src;
                        src++;
                        dst += 3;
                    }

                    dst += dstOffset;
                }
            }

            bitmap.UnlockBits(bmpData);
        }
    }
}
