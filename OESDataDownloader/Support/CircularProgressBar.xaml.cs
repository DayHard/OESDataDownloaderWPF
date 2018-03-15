using System.Windows;
using System.Windows.Media.Animation;

namespace OESDataDownloader.Support
{
    /// <summary>
    /// Interaction logic for CircularProgressBar.xaml
    /// </summary>
    public partial class CircularProgressBar
    {
        static CircularProgressBar()
        {
            //Use a default Animation Framerate of 30, which uses less CPU time
            //than the standard 50 which you get out of the box
            Timeline.DesiredFrameRateProperty.OverrideMetadata(
                typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 25 });
        }

        public CircularProgressBar()
        {
            InitializeComponent();
        }
    }
}


