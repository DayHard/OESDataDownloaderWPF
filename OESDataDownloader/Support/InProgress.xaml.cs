namespace OESDataDownloader.Support
{
    /// <summary>
    /// Логика взаимодействия для InProgress.xaml
    /// </summary>
    public partial class InProgress
    {
        public InProgress(string message)
        {
            InitializeComponent();
            LabCommand.Content = message;
        }

        public void ChangeMessage(string message)
        {
            LabCommand.Content = message;
        }
    }
}
