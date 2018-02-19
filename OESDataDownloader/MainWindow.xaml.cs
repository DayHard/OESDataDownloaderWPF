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
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace OESDataDownloader
{
    /// <summary>
    /// Логика взаимодействия для MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        private readonly Ping _ping;
        private readonly UdpClient _sender;
        private readonly UdpClient _resiver;
        private readonly IPEndPoint _endPoint;
        private IPEndPoint _remoteIpEndPoint;
        private static string _remoteIp;
        private bool _oedIsAvaliable;


        private readonly byte[] _comGetStatus = { 10, 0, 1, 0, 0, 0, 0, 0 };
        private const int RemotePort = 3001;
        //private readonly byte[] _comGetStatus = { 10, 1, 0, 0, 0, 0, 0, 0 };
        //private const int RemotePort = 3000;
        private const int TimeOut = 100;
        private const int LocalPort = 3000;
        private const int ConnectionRetry = 1000;

        private readonly int[] _launchSize = new int[15];
        public MainWindow()
        {
            LoadConfiguration();
            InitializeComponent();
            _ping = new Ping();
            _sender = new UdpClient();
            _resiver = new UdpClient(LocalPort) { Client = { ReceiveTimeout = TimeOut, DontFragment = false } };
            _endPoint = new IPEndPoint(IPAddress.Parse(_remoteIp), RemotePort);
        }
        private void Window_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            LbVersion.Content = "Версия ПО: " + Assembly.GetExecutingAssembly().GetName().Version;
            // Установка таймаута приемника проверки статуса соединения
            var thread = new Thread(CheckNetStatus) { IsBackground = true };
            thread.Start();
        }
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
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
                    break;
                }
               Thread.Sleep(ConnectionRetry); 
            }
        }
        private bool CheckEthernet()
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;
            try
            {
                var pingReply = _ping.Send(_remoteIp, TimeOut);
                if (pingReply != null && pingReply.Status == IPStatus.Success)
                {
                    BtnIndicEthernet.Background = Brushes.GreenYellow;
                    return true;
                }
            }
            catch (Exception ex)
            {
                AddToOperationsPerfomed("Ошибка пинга STM: " + ex.Message);
            }
            return false;
        }
        private bool CheckUsb()
        {
            try
            {
                // Если буфер приема не пустой, очищаем
                if (_resiver.Available > 0)
                {
                    _resiver.Receive(ref _remoteIpEndPoint);
                }

                _sender.Send(_comGetStatus, _comGetStatus.Length, _endPoint);
                var resp = _resiver.Receive(ref _remoteIpEndPoint);
                //Проверка доступности ОЕД 
                if (resp[0] == 10 && resp[2] == 1 && resp[8] == 1)
                {
                    BtnIndicUsb.Background= Brushes.GreenYellow;
                    return true;
                }
            }
            catch (SocketException)
            {
                AddToOperationsPerfomed("STM не вернул статус USB соединения. TimeOut.");
            }
            BtnIndicUsb.Background = Brushes.OrangeRed;
            return false;
        }
        private bool CheckOed()
        {
            try
            {
                var data = GetAllInfo();
                if (data == null)
                    return false;

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

                if (ToBigEndian(data, 2 + 128, 2) != 0 && ToBigEndian(data, 4 + 128, 4) != 0)
                {
                    AddToLaunchInfo(ToBigEndian(data, 2 + 128, 2), ToBigEndian(data, 4 + 128, 4));
                    AddToOperationsPerfomed("Количество записанных диагностик: " + ToBigEndian(data, 2 + 128, 2));
                }

                BtnIndicOed.Background= Brushes.GreenYellow;
                return true;
            }
            catch (SocketException)
            {
                AddToOperationsPerfomed("ОЕД не вернул список пусков. TimeOut.");
            }
                ListBLaunchInfo.Items.Clear();

            BtnIndicOed.Background = Brushes.OrangeRed;
            return false;
        }
        #endregion

        #region ControlsCommands
        /// <summary>
        /// Запрос на получение всех пусков
        /// </summary>
        /// <returns>Массив пусков</returns>
        private byte[] GetAllInfo()
        {
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
            var receivedData = _resiver.Receive(ref _remoteIpEndPoint);

            if (receivedData.Length != 200)
                return null;

            var data = new byte[receivedData.Length - 8];
            Array.Copy(receivedData, 8, data, 0, data.Length);

            return data;
        }
        /// <summary>
        /// Запрос на получение пуска
        /// </summary>
        /// <param name="number">Номер пуска</param>
        /// <returns></returns>
        private Task<byte[]> GetLaunch(int number)
        {
            #region ArrayDefinitions

            // Команда
            // Скачать пуск номер number (привести к byte)
            var preplaunch = new byte[8];
            preplaunch[0] = 12;
            preplaunch[2] = 2;
            preplaunch[7] = (byte)number;

            // Команда
            // Заполнить 2к буффер
            //(прием по 1024 в 2 части)
            var fill2Kbuff = new byte[8];
            fill2Kbuff[0] = 12;
            fill2Kbuff[2] = 3;

            var index = number - 1;
            var counter = 0;
            var data = new byte[_launchSize[index]];
            // Инициализируем массив приема нулями
            for (int i = 0; i < data.Length; i++)
                data[i] = 0;
            // Таймаут приема UDP
            _resiver.Client.ReceiveTimeout = 3000;

            // Очистка буфера приема UDP
            if (_resiver.Available > 0)
            {
                _resiver.Receive(ref _remoteIpEndPoint);
            }
            #endregion

            _sender.Send(preplaunch, preplaunch.Length, _endPoint);
            // Ожидание считывания информации о пуске во Флэш память ОЭД
            Thread.Sleep(10_000);
            do
            {
                _sender.Send(fill2Kbuff, fill2Kbuff.Length, _endPoint);
                Thread.Sleep(4);
                {
                    try
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
                        }
                    }
                    catch (SocketException)
                    {
                        AddToOperationsPerfomed("Ошибка считывания пуска с ОЕД. TimeOut.");
                    }
                }
            } while (_launchSize[index] > counter);

            return data;
        }
        #endregion

        #region LanguageConfiguration
        // Переключение локализации на русский
        private void btnLangRus_Click(object sender, EventArgs e)
        {
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("ru-RU");
            InitializeComponent();
        }
        // Переключение локализации на французкий
        private void btnLangFr_Click(object sender, EventArgs e)
        {
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("fr-FR");
            InitializeComponent();
        }
        // Переключение локализации на английский
        private void btnLangEng_Click(object sender, EventArgs e)
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
            Dispatcher.Invoke(new Action(() =>
            {
                ListBOperationsPerfomed.Items.Add(@"[" + DateTime.Now + @"] " + message);
                object lastItem = ListBOperationsPerfomed.Items[ListBOperationsPerfomed.Items.Count - 1];
                ListBOperationsPerfomed.Items.MoveCurrentTo(lastItem);
                ListBOperationsPerfomed.ScrollIntoView(lastItem);
            }));
        }
        /// <summary>
        /// Информация о пуске (использует Invoke)
        /// </summary>
        /// <param name="launch">Пуск</param>
        /// <param name="sizepack">Количество пакетов</param>
        /// <param name="size">Размер</param>
        private void AddToLaunchInfo(byte launch, int sizepack, int size)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                ListBLaunchInfo.Items.Add(@"Пуск: " + launch + @" Количество пакетов: " + sizepack + @" Размер: " + size + " B");
            }));
        }
        /// <summary>
        /// Информация о диагностике (использует Invoke)
        /// </summary>
        /// <param name="sizediag">Количесвто диагностик</param>
        /// <param name="size"> Размер диагностик в B</param>
        private void AddToLaunchInfo(int sizediag, int size)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                ListBLaunchInfo.Items.Add(@"Количество диагностик: " + sizediag + @" Размер: " + size + " B");
            }));
        }
        /// <summary>
        /// Добавление нового сохраненного файла
        /// </summary>
        /// <param name="index">Номер пуска</param>
        private void AddToSavedInfo(int index)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                ListBSavedInfo.Items.Add("Пуск номер: " + index);
            }));
        }
        #endregion

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

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!_oedIsAvaliable) return;
            var index = ListBLaunchInfo.SelectedIndex;
            var launch = index + 1;
            if (index != -1)
            {
                LbBytesReceived.Content = @"0/" + _launchSize[index];
                PbDownloadStatus.Visibility = Visibility.Visible;
                PbDownloadStatus.Value = 0;
                PbDownloadStatus.Maximum = _launchSize[index];

                GetLaunchAsync(launch);

            }
            else MessageBox.Show("Выберите пуск для скачивания!");
        }

        private async void GetLaunchAsync(int launch)
        {
            var data = await GetLaunch(launch);
            using (var bw = new BinaryWriter(new FileStream(launch + ".imi", FileMode.OpenOrCreate)))
            {
                bw.Write(data);
            }
            AddToSavedInfo(launch);
        }
    }
}
