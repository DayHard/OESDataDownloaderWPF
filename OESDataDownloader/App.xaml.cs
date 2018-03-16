using System.Threading;
using System.Windows;

namespace OESDataDownloader
{
    /// <summary>
    /// Логика взаимодействия для App.xaml
    /// </summary>
    public partial class App : Application
    {
        static Mutex InstanceCheckMutex;
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            if (!InstanceCheck())
            {
                MessageBox.Show("Программа уже запущена!", "Ошибка", MessageBoxButton.OK);
                System.Environment.Exit(1);
            }

        }
        private static bool InstanceCheck()
        {
            bool isNew;
            InstanceCheckMutex = new Mutex(true, "OESDataDownloader", out isNew);
            return isNew;
        }
    }
}
