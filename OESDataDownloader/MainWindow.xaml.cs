using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using OESDataDownloader.Support;

namespace OESDataDownloader
{
    /// <summary>
    /// Логика взаимодействия для MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {

        #region Variables

        private readonly Ping _ping;
        private readonly UdpClient _sender;
        private readonly UdpClient _resiver;
        private readonly IPEndPoint _endPoint;
        private InProgress _inProgress;
        private IPEndPoint _remoteIpEndPoint;
        private CancellationTokenSource _cts;
        private static readonly DispatcherTimer Timer = new DispatcherTimer {Interval = TimeSpan.FromSeconds(1)};
        private static readonly DispatcherTimer DoNotCloseConnectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1)};
        private static DateTime _timepassed;
        private static string _remoteIp;
        private Thread _thdCheckNet;
        private AutoResetEvent _waitHandle;
        private bool _oedIsAvaliable;
        private bool _diagIsAvaliable;

        private readonly byte[] _comGetStatus = { 10, 0, 1, 0, 0, 0, 0, 0 };
        private readonly byte[] _doNotClose = {10, 0, 0, 0, 0, 0, 0, 0};
        private const int RemotePort = 40101;
        //private readonly byte[] _comGetStatus = { 10, 1, 0, 0, 0, 0, 0, 0 };
        //private const int RemotePort = 3000;
        private const int TimeOut = 100;
        private const int LocalPort = 40100;
        private const int ConnectionRetry = 1000;
        private const int ResiveTimeOut = 1000;

        private readonly int[] _launchSize = new int[15];

        #endregion
        public MainWindow()
        {
            // Загружаем файл локализации и Ip-адреса STM
            LoadConfiguration();
            // Инициализация компонентов формы
            InitializeComponent();
            // Установка состоянии элементов интерфейса в режиме ожидания
            SetControlsReady();

            _ping = new Ping();
            _sender = new UdpClient();
            _resiver = new UdpClient(LocalPort) { Client = { ReceiveTimeout = TimeOut, DontFragment = false } };
            _endPoint = new IPEndPoint(IPAddress.Parse(_remoteIp), RemotePort);
            // Подпись на событие, для секундомера
            Timer.Tick += Timer_Tick;
            // Подпись на событие, поддержания связи с STM
            DoNotCloseConnectionTimer.Tick += DoNotCloseConnetionTimer_Tick;
            // Запуск таймера
            DoNotCloseConnectionTimer.Start();
        }
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LbVersion.Content = "Версия ПО: " + Assembly.GetExecutingAssembly().GetName().Version;
            // Скрываем путь к сохраненному файлу
            LabSavedFilesPaths.Visibility = Visibility.Hidden;
            // Установка таймаута приемника проверки статуса соединения
            _thdCheckNet = new Thread(CheckNetStatus) {IsBackground = true};
            _waitHandle = new AutoResetEvent(true);
            _thdCheckNet.Start();
        }
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Закрытие формы загрузки
            _inProgress?.Close();
            // Остановка отправки команды на поддержания соединения
            DoNotCloseConnectionTimer.Stop();
            //Закрытие портов перед выходом из программы
            _resiver.Close();
            _sender.Close();
            // Обновление файла конфигурации
             UpdateConfiguration();
        }

        #region NETStatus

        private void CheckNetStatus()
        {
            for (;;)
            {
                if (CheckEthernet() && CheckUsb() && CheckOed())
                {
                    _oedIsAvaliable = true;
                    _waitHandle.WaitOne();
                }
               Thread.Sleep(ConnectionRetry); 
            }
            // ReSharper disable once FunctionNeverReturns
        }
        private bool CheckEthernet()
        {
            try
            {
                var pingReply = _ping.Send(_remoteIp, TimeOut);
                switch (pingReply?.Status)
                {
                case IPStatus.Success:
                    Dispatcher.Invoke(() =>{ BtnIndicEthernet.Background = Brushes.GreenYellow; });
                    return true;
                case IPStatus.TimedOut:
                        AddToOperationsPerfomed("Ошибка пинга STM. TimeOut.");
                        break;
                default:
                        AddToOperationsPerfomed("Неизвестная ошибка, при попытке пинга.");
                        break;
                }
            }
            catch (Exception ex)
            {
                AddToOperationsPerfomed("Ошибка: " + ex.Message);
            }
            Dispatcher.Invoke(() => 
            {
                BtnIndicEthernet.Background = Brushes.OrangeRed;
                BtnIndicUsb.Background = Brushes.OrangeRed;
                BtnIndicOed.Background = Brushes.OrangeRed;
            });
            return false;
        }
        private bool CheckUsb()
        {
            try
            {
                // Если буфер приема не пустой, очищаем
                if (_resiver != null && _resiver?.Available > 0)
                {
                    _resiver.Receive(ref _remoteIpEndPoint);
                }

                _sender.Send(_comGetStatus, _comGetStatus.Length, _endPoint);
                var resp = _resiver?.Receive(ref _remoteIpEndPoint);
                //Проверка доступности ОЕД 
                if (resp != null && resp[0] == 10 && resp[2] == 1 && resp[8] == 1)
                {
                    Dispatcher.Invoke(() =>
                    {
                        BtnIndicUsb.Background = Brushes.GreenYellow;
                    });
                    return true;
                }
                    AddToOperationsPerfomed("USB не доступен.");
            }
            catch (SocketException)
            {
                AddToOperationsPerfomed("STM не вернул статус USB соединения. TimeOut.");
            }         
            Dispatcher.Invoke(() => 
            {
                BtnIndicUsb.Background = Brushes.OrangeRed;
                BtnIndicOed.Background = Brushes.OrangeRed;
            }); 
            return false;
        }
        private bool CheckOed()
        {
            try
            {
                _diagIsAvaliable = false;
                // Очистим список загруженных пусков
                Dispatcher.Invoke(() => {ListBLaunchInfo.Items.Clear();});

                var data = GetAllInfo();

                if (data == null)
                {
                    AddToOperationsPerfomed("STM завис!");
                    return false;
                }
                // Удаление/Форматирование Flash ОЭД в процессе
                if (data.Length == 8 && data[3] == 0xff)
                {
                    Dispatcher.Invoke(() => { BtnIndicOed.Background = Brushes.Yellow; });
                    return false;
                }

                if (ToLittleEndian(data, 5, 2) == 0x1506)
                {
                    for (int i = 1; i <= data[4]; i++)
                    {
                        _launchSize[i - 1] = ToLittleEndian(data, i * 7 + 3, 4);
                        AddToLaunchInfo(data[i * 7], ToLittleEndian(data, i * 7 + 1, 2), _launchSize[i - 1]);
                    }

                    AddToOperationsPerfomed("Соединение с STM установлено.");
                    AddToOperationsPerfomed("Соединение по USB установлено.");
                    AddToOperationsPerfomed("Пуски считаны.");
                    AddToOperationsPerfomed("Номер прибора: " + ToLittleEndian(data, 0, 4));
                    AddToOperationsPerfomed("Количество записанных пусков: " + data[4]);
                }

                if (data.Length != 8 && ToBigEndian(data, 2 + 128, 2) != 0 && ToBigEndian(data, 4 + 128, 4) != 0)
                {
                    _launchSize[_launchSize.Length - 1] = ToBigEndian(data, 4 + 128, 4);
                    AddToLaunchInfo(ToBigEndian(data, 2 + 128, 2), ToBigEndian(data, 4 + 128, 4));
                    AddToOperationsPerfomed("Количество записанных диагностик: " + ToBigEndian(data, 2 + 128, 2));
                    _diagIsAvaliable = true;
                }
                Dispatcher.Invoke(() => { BtnIndicOed.Background = Brushes.GreenYellow; });
                return true;
            }
            catch (SocketException)
            {
                AddToOperationsPerfomed("ОЕД не вернул список пусков. TimeOut.");
                Dispatcher.Invoke(() =>
                {
                    ListBLaunchInfo.Items.Clear();
                    BtnIndicOed.Background = Brushes.OrangeRed;
                });
                return false;
            }
        }

        #endregion

        #region ControlsCommands

        /// <summary>
        /// Запрос на получение всех пусков
        /// </summary>
        /// <returns>Массив пусков</returns>
        private byte[] GetAllInfo()
        {
            if (_resiver == null)
                return null;

            // Формирование команды получения всех пусков
            var request = new byte[8];
            request[0] = 12;// ОЭД
            request[2] = 1;

            // Очистка буфера приема UDP
            if (_resiver.Available > 0)
            {
                _resiver.Receive(ref _remoteIpEndPoint);
            }

            _sender.Send(request, request.Length, _endPoint);

            _resiver.Client.ReceiveTimeout = ResiveTimeOut;

            try
            {
                var receivedData = _resiver.Receive(ref _remoteIpEndPoint);

                // Если оэд не отвечает
                if (receivedData[3] == 0xff)
                    return receivedData;

                if (receivedData.Length != 200)
                    return null;

                var data = new byte[receivedData.Length - 8];
                Array.Copy(receivedData, 8, data, 0, data.Length);
                return data;
            }
            catch (SocketException)
            {
                AddToOperationsPerfomed("Ошибка. Список пусков не получен. TimeOut.");
                return null;
            }
        }
        /// <summary>
        /// Скачать пуск(запускать в отдельном потомке\задаче)
        /// </summary>
        /// <param name="launch">Номер пуска</param>
        /// <param name="path">Путь сохранения пуска</param>
        /// <param name="ctsToken">Токен отмены</param>
        private void GetLaunch(int launch, string path, CancellationToken ctsToken)
        {
            byte[] data = GetLaunchFromStm(launch, ctsToken);

            if (data == null) return;

            // Если качаем диагностику, устанавливаем имя файла diagnostics
            var fileName = launch == 0xDA ? "diagnostics" : launch.ToString();

            string fullPath = path + @"\" + fileName + ".imi";
            using (var bw = new BinaryWriter(new FileStream(fullPath, FileMode.OpenOrCreate)))
            {
                bw.Write(data);
            }

            AddToSavedInfo(fileName);
            Dispatcher.Invoke(() =>
            {
                LabSavedFilesPaths.Visibility = Visibility.Visible;
                LabSavedFilesPaths.Content = "Расположение сохраняемых файлов: " + @"\\" + path + @"\\";
            });

        }
        /// <summary>
        /// Запрос на получение пуска из STM
        /// </summary>
        /// <param name="number">Номер пуска</param>
        /// <param name="ctsToken">Токен отмены задачи</param>
        /// <returns></returns>
        private byte[] GetLaunchFromStm(int number, CancellationToken ctsToken)
        {
            #region LocalDefinitions

            // Команда
            // Скачать пуск номер number (привести к byte)
            var preplaunch = new byte[9];
            preplaunch[0] = 12;
            preplaunch[2] = 2;
            preplaunch[8] = (byte)number;

            // Команда
            // Заполнить 2к буффер
            //(прием по 1024 в 2 части)
            var fill2Kbuff = new byte[8];
            fill2Kbuff[0] = 12;
            fill2Kbuff[2] = 3;

            int index, counter = 0;

            if (number == 0xda)
                 index = _launchSize.Length - 1;
            else index = number - 1;

            var data = new byte[_launchSize[index]];
            // Инициализируем массив приема нулями
            for (int i = 0; i < data.Length; i++)
                data[i] = 0;
            // Таймаут приема UDP
            _resiver.Client.ReceiveTimeout = ResiveTimeOut;

            // Очистка буфера приема UDP
            if (_resiver != null && _resiver?.Available > 0)
            {
                _resiver.Receive(ref _remoteIpEndPoint);
            }
            #endregion

            try
            {
                _sender.Send(preplaunch, preplaunch.Length, _endPoint);
                // Ожидание считывания информации о пуске во Флэш память ОЭД
                // Каждые 100мс проверяем команду отмены
                for (int i = 0; i < 100; i++)
                {
                    Thread.Sleep(100);
                    ctsToken.ThrowIfCancellationRequested();
                }

                do
                {
                    _sender.Send(fill2Kbuff, fill2Kbuff.Length, _endPoint);
         
                    {
                        for (int i = 0; i < 2; i++)
                        {
                            var rdata = _resiver.Receive(ref _remoteIpEndPoint);
                            if (data.Length - counter >= 1024)
                            {
                                Array.Copy(rdata, 8, data, counter, rdata.Length - 8);
                                counter += rdata.Length - 8;
                            }
                            else
                            {
                                Array.Copy(rdata, 8, data, counter, data.Length - counter);
                                counter += data.Length - counter;
                            }
                            var counter1 = counter;
                            Dispatcher.Invoke(() =>
                            {
                                PbDownloadStatus.Value = counter1;
                                LbBytesReceived.Content = counter1 + "/" + _launchSize[index];
                            });
                        }

                    }
                    // Проверяем задачу на токен отмены
                    ctsToken.ThrowIfCancellationRequested();
                } while (_launchSize[index] > counter);
            }
            catch (SocketException)
            {
                AddToOperationsPerfomed("Ошибка считывания пуска с ОЕД. TimeOut.");
            }
            catch (OperationCanceledException)
            {
                AddToOperationsPerfomed("Скачивание отмененно.");
                return null;
            }
            return data;
        }
        /// <summary>
        /// Удалить все пуски (запускать в отдельном потомке\задаче)
        /// </summary>
        private void DeleteAllLaunches()
        {
            _oedIsAvaliable = false;
            byte[] deleteAllLaunches = new byte[8];
            deleteAllLaunches[0] = 12;
            deleteAllLaunches[2] = 4;

            _sender.Send(deleteAllLaunches, deleteAllLaunches.Length, _endPoint);
            _waitHandle.Set();

            while (true)
            {
                if (_oedIsAvaliable)
                    break;
                Thread.Sleep(ConnectionRetry);
            }
        }
        /// <summary>
        /// Отморматирвоть ОЭД (запускать в отдельном потомке\задаче)
        /// </summary>
        private void FormatOed()
        {
            _oedIsAvaliable = false;
            byte[] formatOed = new byte[8];
            formatOed[0] = 12;
            formatOed[2] = 5;

            _sender.Send(formatOed, formatOed.Length, _endPoint);
            _waitHandle.Set();

            while (true)
            {
                if (_oedIsAvaliable)
                    break;
                Thread.Sleep(ConnectionRetry);
            }
        }
        #endregion

        #region LanguageConfiguration
        // Переключение локализации на русский
        private void BtnLangRus_Click(object sender, RoutedEventArgs e)
        {
            var call = MainContainer.Children;
            call.Clear();
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru-RU");
            InitializeComponent();
        }
        // Переключение локализации на французкий
        private void BtnLangFr_Click(object sender, RoutedEventArgs e)
        {
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("fr-FR");
            InitializeComponent();
        }
        // Переключение локализации на английский
        private void BtnLangEng_Click(object sender, RoutedEventArgs e)
        {
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
            InitializeComponent();
        }
        // Загрузка файла setting для установки текущей культуры
        private static void LoadConfiguration()
        {
            if (File.Exists("setting.xml"))
            {
                XDocument xDoc = XDocument.Load("setting.xml");
                XElement xLang = xDoc.Descendants("Language").First();
                ChooseLanguage(xLang.Value);
                // Установка ClientIP
                XElement xClientIp = xDoc.Descendants("ClientIP").First();
                _remoteIp = xClientIp.Value;
                if(_remoteIp == string.Empty) _remoteIp = "192.168.0.100";
            }
            else
            {
                ChooseLanguage(CultureInfo.CurrentCulture.Name);
                _remoteIp = "192.168.0.100";
            }
        }
        //Выбор текущего языка
        private static void ChooseLanguage(string lang)
        {
            switch (lang)
            {
                case "ru-RU":
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru-RU");
                    break;
                case "ru-BY":
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru-RU");
                    break;
                case "en-US":
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
                    break;
                case "fr-FR":
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo("fr-FR");
                    break;
                default:
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru-BY");
                    break;
            }
        }
        // Обновление файла конфигурации
        private static void UpdateConfiguration()
        {
            XmlTextWriter writer = new XmlTextWriter("setting.xml", Encoding.UTF8);
            writer.WriteStartDocument();
            writer.WriteStartElement("xml");
            writer.WriteEndElement();
            writer.Close();

            XmlDocument doc = new XmlDocument();
            doc.Load("setting.xml");
            XmlNode element = doc.CreateElement("OESDataDownloader");
            doc.DocumentElement?.AppendChild(element);

            XmlNode languageEl = doc.CreateElement("Language"); // даём имя
            languageEl.InnerText = Thread.CurrentThread.CurrentUICulture.ToString(); // и значение
            element.AppendChild(languageEl); // и указываем кому принадлежит

            XmlNode clientIpEl = doc.CreateElement("ClientIP"); // даём имя
            clientIpEl.InnerText = _remoteIp; // и значение
            element.AppendChild(clientIpEl); // и указываем кому принадлежит

            doc.Save("setting.xml");
        }

        #endregion

        #region AddToList    
        
        /// <summary>
        /// Служебное сообщение (использует Invoke)
        /// </summary>
        /// <param name="message">Текст сообщения</param>
        private void AddToOperationsPerfomed(string message)
        {
            Dispatcher.Invoke(() =>
            {
                ListBOperationsPerfomed.Items.Add(@"[" + DateTime.Now + @"] " + message);
                object lastItem = ListBOperationsPerfomed.Items[ListBOperationsPerfomed.Items.Count - 1];
                ListBOperationsPerfomed.Items.MoveCurrentTo(lastItem);
                ListBOperationsPerfomed.ScrollIntoView(lastItem);
            });
        }
        /// <summary>
        /// Информация о пуске (использует Invoke)
        /// </summary>
        /// <param name="launch">Пуск</param>
        /// <param name="sizepack">Количество пакетов</param>
        /// <param name="size">Размер</param>
        private void AddToLaunchInfo(byte launch, int sizepack, int size)
        {
            Dispatcher.Invoke(() =>{ ListBLaunchInfo.Items.Add(@"Пуск: " + launch + @" Количество пакетов: " + sizepack + @" Размер: " + size + " B"); });
        }
        /// <summary>
        /// Информация о диагностике (использует Invoke)
        /// </summary>
        /// <param name="sizediag">Количесвто диагностик</param>
        /// <param name="size"> Размер диагностик в B</param>
        private void AddToLaunchInfo(int sizediag, int size)
        {
            Dispatcher.Invoke(() =>{ ListBLaunchInfo.Items.Add(@"Количество диагностик: " + sizediag + @" Размер: " + size + " B"); });
        }
        /// <summary>
        /// Добавление нового сохраненного файла
        /// </summary>
        /// <param name="info">Номер пуска</param>
        private void AddToSavedInfo(string info)
        {
            Dispatcher.Invoke(() =>{ ListBSavedInfo.Items.Add(info + ".imi"); });
        }

        #endregion

        #region SuportMethods

        /// <summary>
        /// Установка состоянии элементов интерфейса в режиме ожидания
        /// </summary>
        private void SetControlsReady()
        {
            // Сброс пройденного времени с начала закачки
            _timepassed = new DateTime();
            LbTimeEllapsed.Content = "Прошло: " + _timepassed.ToString("mm:ss");
            Timer.Stop();            

            BtnDeleteAll.IsEnabled = true;
            BtnFormating.IsEnabled = true;
            BtnSave.IsEnabled = true;
            PbDownloadStatus.Visibility = Visibility.Hidden;
            BtnCancelDownload.Visibility = Visibility.Hidden;
            LbTimeEllapsed.Visibility = Visibility.Hidden;
            LbBytesReceived.Visibility = Visibility.Hidden;
        }
        /// <summary>
        /// Установка состояния эелемента интерфейса при загрузке пусков
        /// </summary>
        private void SetControlsDownloading()
        {
            Timer.Start();

            BtnDeleteAll.IsEnabled = false;
            BtnFormating.IsEnabled = false;
            BtnSave.IsEnabled = false;
            PbDownloadStatus.Visibility = Visibility.Visible;
            BtnCancelDownload.Visibility = Visibility.Visible;
            LbTimeEllapsed.Visibility = Visibility.Visible;
            LbBytesReceived.Visibility = Visibility.Visible;
        }
        /// <summary>
        /// Преобразование к порядку байтов Little Endian
        /// </summary>
        /// <param name="data">Массив</param>
        /// <param name="poss">Положение 1 байта числа</param>
        /// <param name="size">Размер числа в байтах</param>
        /// <returns></returns>
        private static int ToLittleEndian(byte[] data, int poss, int size)
        {
            var arr = new byte[size];
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = data[poss + i];
            }
            return arr.Select((t, i) => t << i * 8).Sum();
        }
        /// <summary>
        /// Преобразование к порядку байтов Big Endian
        /// </summary>
        /// <param name="data">Массив</param>
        /// <param name="poss">Положение 1 байта числа</param>
        /// <param name="size">Размер числа в байтах</param>
        /// <returns></returns>
        private static int ToBigEndian(byte[] data, int poss, int size)
        {
            var arr = new byte[size];
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = data[poss + i];
            }
            int result = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                result |= arr[i] << 8 * (size - 1 - i);
            }
            return result;
        }

        // Таймер, считаеющий сколько прошло время с начала загрузки
        private void Timer_Tick(object sender, EventArgs e)
        {
            _timepassed = _timepassed.AddSeconds(1);
            Dispatcher.Invoke(() =>
            {
                LbTimeEllapsed.Content = "Прошло: " + _timepassed.ToString("mm:ss");
            });
        }

        // Команда STM не закрывать соединение
        private void DoNotCloseConnetionTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                _sender.Send(_doNotClose, _doNotClose.Length, _endPoint);
            }
            catch (Exception ex)
            {
                AddToOperationsPerfomed(ex.Message);
            }
        }
        // Сгенерировать пуски
        private void GenerateLaunches_Click(object sender, RoutedEventArgs e)
        {
            _oedIsAvaliable = true;
            _diagIsAvaliable = true;

            AddToLaunchInfo(1, 10, 100);
            AddToLaunchInfo(2, 20, 200);
            AddToLaunchInfo(3, 30, 300);
            AddToLaunchInfo(4, 40, 400);
            AddToLaunchInfo(5, 50, 500);
            AddToLaunchInfo(6, 60, 600);
            AddToLaunchInfo(7, 70, 700);
            AddToLaunchInfo(8, 80, 800);
            AddToLaunchInfo(9, 90, 900);
            //AddToLaunchInfo(10, 100, 1000);
            //AddToLaunchInfo(11, 110, 1200);
            //AddToLaunchInfo(12, 120, 1300);
            //AddToLaunchInfo(13, 120, 1400);
            AddToLaunchInfo(5, 50);
        }
        // Показать элементы загрузки
        private void ShowDowmloadingControls_Click(object sender, RoutedEventArgs e)
        {
            SetControlsDownloading();
        }
        // Скрыть элементы загрузки
        private void HideDownloadingControls_Click(object sender, RoutedEventArgs e)
        {
            SetControlsReady();
        }
        // Показать форму загрузки
        private void CheckLW_Click(object sender, RoutedEventArgs e)
        {
            if (_inProgress == null)
            {
                _inProgress = new InProgress("Удаление")
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                _inProgress.Show();
            }
            else
            {
                _inProgress.ChangeMessage("Форматирвание");
                // Задаем владельца формы
                _inProgress.Show();
            }
        }
        // Скрытый функционал для разработчиков по нажатию Ctrl + Shift + L
        private void HiddenOpportunities(object sender, KeyEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftShift) && Keyboard.IsKeyDown(Key.L))
            {
                ShowDowmloadingControls.Visibility = ShowDowmloadingControls.Visibility == Visibility.Hidden
                    ? Visibility.Visible
                    : Visibility.Hidden;
                HideDownloadingControls.Visibility = HideDownloadingControls.Visibility == Visibility.Hidden
                    ? Visibility.Visible
                    : Visibility.Hidden;
                GenerateLaunches.Visibility = GenerateLaunches.Visibility == Visibility.Hidden
                    ? Visibility.Visible
                    : Visibility.Hidden;
                CheckLw.Visibility = CheckLw.Visibility == Visibility.Hidden
                    ? Visibility.Visible
                    : Visibility.Hidden;
            }

            // Альтернативное решение
            //if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            //    (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift &&
            //    e.Key == Key.L)
        }
        /// <summary>
        /// Показать анимацию загрузки
        /// </summary>
        /// <param name="message">Сообщение</param>
        private void ShowWorkInProgress(string message)
        {
            if (_inProgress == null)
            {
                _inProgress = new InProgress(message)
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                _inProgress.Show();
            }
            else
            {
                _inProgress.ChangeMessage(message);
                // Задаем владельца формы
                _inProgress.Show();
            }
        }

        #endregion

        // Скачать пуск номер №
        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            // Проверяем, доступен ли ОЕД
            if (!_oedIsAvaliable) return;

            // Создаем папку для сохранения пусков текущей сессии
            var path = DateTime.Now.ToShortDateString() + " Time " + DateTime.Now.Hour + " " + DateTime.Now.Minute + " " + DateTime.Now.Second;
            Directory.CreateDirectory(path);

            var seletedIndexes = new int[ListBLaunchInfo.SelectedItems.Count];

            var k = 0;
            // Создаем массив, с номерами выделеных пусков
            foreach (var item in ListBLaunchInfo.SelectedItems)
            {
                seletedIndexes[k] = ListBLaunchInfo.Items.IndexOf(item);
                k++;
            }

            for (int i = 0; i < seletedIndexes.Length; i++)
            {
                var launch = seletedIndexes[i] + 1;

                if (_diagIsAvaliable && seletedIndexes[i] == ListBLaunchInfo.Items.Count - 1)
                    launch = _launchSize.Length;

                if (launch != -1)
                {
                    LbBytesReceived.Content = @"0/" + _launchSize[launch - 1];
                    PbDownloadStatus.Visibility = Visibility.Visible;
                    PbDownloadStatus.Value = 0;
                    PbDownloadStatus.Maximum = _launchSize[launch - 1];

                    SetControlsDownloading();

                    // Для скачивания диагностики
                    if (_diagIsAvaliable && launch == 15)
                        launch = 0xDA;

                    // Создаем токен отмены задачи
                    _cts = new CancellationTokenSource();
                    await Task.Run(() => GetLaunch(launch, path, _cts.Token), _cts.Token);

                    SetControlsReady();
                }
                else MessageBox.Show("Выберите пуск для скачивания!");
            }
        }
        // Отменить скачивание пуска
        private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
        {
            _cts.Cancel();
        }
        // Удалить все пуски
        private async void BtnDeleteAll_Click(object sender, RoutedEventArgs e)
        {
            // Проверяем, доступен ли ОЕД
            if (!_oedIsAvaliable) return;

            // Подтверждение удаления пусков
            if (MessageBox.Show("Вы уверены, что хотите удалить все пуски?", "Внимание", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                return;
            ShowWorkInProgress("Удаление");

            await Task.Run(() => DeleteAllLaunches());

            _inProgress?.Hide();

            if (_oedIsAvaliable)
            {
                AddToOperationsPerfomed("Удаление произведено успешно.");
                MessageBox.Show("Все пуски были успешно удалены.");
            }
            else
            {
                AddToOperationsPerfomed("Ошибка удаления пусков из ОЭД.");
                MessageBox.Show("В ходе удаления пусков произошла неизвестная ошибка.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }

        }
        // Форматировать ОЭД
        private async void BtnFormating_Click(object sender, RoutedEventArgs e)
        {
            // Проверяем, доступен ли ОЕД
            if (!_oedIsAvaliable) return;

            // Подтверждение форматирования Flash
            if (MessageBox.Show("Вы уверены, что хотите отформатировать FLASH?", "Внимание", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                return;
            ShowWorkInProgress("Форматирование");

            await Task.Run(() => FormatOed());

            _inProgress?.Hide();

            if (_oedIsAvaliable)
            {
                AddToOperationsPerfomed("Форматирование произведено успешно.");
                MessageBox.Show("ОЭД успешно отформатирован.");
            }
            else
            {
                AddToOperationsPerfomed("Ошибка форматирования ОЭД.");
                MessageBox.Show("В ходе форматирования произошла неизвестная ошибка.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }

        }
    }
}
