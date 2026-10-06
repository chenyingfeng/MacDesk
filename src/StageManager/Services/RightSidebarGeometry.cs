using System;
using System.Windows;

namespace StageManager.Services
{
    internal static class RightSidebarGeometry
    {
        internal static double PhysicalLeft(double right, double widthDip, double dpi)
            => right-widthDip*dpi;
        internal static double OutwardDistance(double widthDip, double localX)
            => widthDip-localX;
        internal static double BufferProgress(double cursor, double bufferLeft, double sidebarLeft)
            => Math.Clamp((cursor-bufferLeft)/(sidebarLeft-bufferLeft),0,1);
        internal static Rect DropBounds(Rect work,Point cursor,Size requested)
        {
            double gap=12;
            double width=Math.Clamp(requested.Width,1,Math.Max(1,work.Width-2*gap));
            double height=Math.Clamp(requested.Height,1,Math.Max(1,work.Height-2*gap));
            double x=Math.Clamp(cursor.X-width/2,work.Left+gap,Math.Max(work.Left+gap,work.Right-width-gap));
            double y=Math.Clamp(cursor.Y-16,work.Top+gap,Math.Max(work.Top+gap,work.Bottom-height-gap));
            return new Rect(x,y,width,height);
        }
        internal static Rect[] TileBounds(Rect work,int count)
        {
            if(count<=0)return Array.Empty<Rect>();
            int columns=(int)Math.Ceiling(Math.Sqrt(count));
            int rows=(int)Math.Ceiling((double)count/columns);
            double gap=Math.Min(10,Math.Min(work.Width/(columns*4),work.Height/(rows*4)));
            double width=(work.Width-gap*(columns+1))/columns;
            double height=(work.Height-gap*(rows+1))/rows;
            var result=new Rect[count];
            for(int i=0;i<count;i++) result[i]=new Rect(work.Left+gap+(i%columns)*(width+gap),
                work.Top+gap+(i/columns)*(height+gap),width,height);
            return result;
        }
    }
}
