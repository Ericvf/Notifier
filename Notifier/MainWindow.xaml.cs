using AnimationExtensions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Media;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Timer = System.Timers.Timer;

namespace Notifier
{

    public partial class MainWindow : Window
    {
        private static int portNo = 83; 

        WebServer w = new WebServer();
        MainVM vm;

        Timer timer;

        public MainWindow()
        {
            InitializeComponent();
            this.SetupWindow();
            this.SetupTimer();

            vm = new MainVM(this.LayoutRoot);
            vm.Update($"Notifier started&This app will run in the background on port {portNo}");
            LayoutRoot.Width = 0;
            DataContext = vm;

            w.ReceivedCommand += (s, e) => {
                if (!this.IsVisible)
                    this.Show();
                vm.Update(e);
                timer.Stop();
                timer.Start();
            };
            w.Start(portNo);

            Closing += (s, e) =>
            {
                w.Stop();
                w.Dispose();
            };
        }

        private void SetupTimer()
        {
            timer = new Timer();
            timer.Interval = 5000;
            timer.Elapsed += Timer_Elapsed;
        }

        private void Timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            timer.Stop();
            Dispatcher.BeginInvoke(new Action(() => this.Hide()));
        }

        private void SetupWindow()
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 31, 31, 31));

            Left = SystemParameters.PrimaryScreenWidth - Width - 10;
            Top = SystemParameters.PrimaryScreenHeight - Height - 50;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Topmost = true;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                    this.Close();
            };

            MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    this.vm.Pause();
                    this.DragMove();
                    this.vm.Resume();
                }

                if (e.ChangedButton == MouseButton.Middle)
                {
                    Left = SystemParameters.PrimaryScreenWidth - Width - 10;
                    Top = SystemParameters.PrimaryScreenHeight - Height - 50;
                }
            };
        }
    }

    static class Animations
    {
        public static Prototype In(this FrameworkElement element)
        {
            return element.Size(0).Size(400, duration: 500, eq: Eq.OutCirc);
        }
        public static Prototype Out(this FrameworkElement element)
        {
            return element.Size(400).Size(0, duration: 500, eq: Eq.OutCirc);
        }
    }

    class MainVM : INotifyPropertyChanged
    {
        readonly FrameworkElement LayoutRoot;
        string previousInput;
        Animation ax;

        #region Properties
        public string Title
        {
            get { return _Title; }
            set
            {
                if (_Title != value)
                {
                    _Title = value;
                    OnPropertyChanged(TitlePropertyName);
                }
            }
        }
        private string _Title;
        public const string TitlePropertyName = "Title";

        public string Body
        {
            get { return _Body; }
            set
            {
                if (_Body != value)
                {
                    _Body = value;
                    OnPropertyChanged(BodyPropertyName);
                }
            }
        }
        private string _Body;
        public const string BodyPropertyName = "Body";
        #endregion

        public MainVM(FrameworkElement element)
        {
            LayoutRoot = element;
        }

        public void Update(string input)
        {
            SystemSounds.Asterisk.Play();
            if (string.IsNullOrEmpty(input))
                return;

            var parts = input.Split('&');
            if (parts.Length > 1)
            {
                this.Title = parts[0];
                this.Body = parts[1].Replace("#", Environment.NewLine);
            }
            else {
                this.Title = input;
            }

            var e = LayoutRoot.FindChilden<TextBlock>().First();
            var e2 = LayoutRoot.FindChilden<TextBlock>().Skip(1).First();
            if (ax == null || ax.IsFinished)
            {
                ax = Ax.New()
                    .And(LayoutRoot.In())
                    .And(e.Fade(0).Move(20).Wait(200).Fade(1, 500).Move(0, 0, 500, Eq.OutBack))
                    .And(e2.Fade(0).Move(20).Wait(300).Fade(1, 500).Move(0, 0, 500, Eq.OutBack))
                    .Wait(3000)
                    .And(LayoutRoot.Out())
                    .Play();
            }
            else
            {
                ax.Stop();

                LayoutRoot.SetSize(400);
                e.Opacity = e2.Opacity = 1;

                if (previousInput == input)
                    LayoutRoot.Move(10, duration: 100, eq:Eq.InSine)
                        .Then()
                        .Move(0, duration: 100, eq: Eq.InSine).Play();

                ax = Ax.New()
                    .Wait(3000)
                    .And(LayoutRoot.Out())
                    .Play();
            }

            previousInput = input;
        }

        public void Pause()
        {
            if (this.ax != null)
                ax.Stop();
        }

        public void Resume()
        {
            ax = LayoutRoot
                .Size(0, duration: 500, eq: Eq.OutCirc)
                .Play();
        }

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion
    }

    class HttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _listenerThread;
        private readonly Thread[] _workers;
        private readonly ManualResetEvent _stop, _ready;
        private Queue<HttpListenerContext> _queue;

        public HttpServer(int maxThreads)
        {
            _workers = new Thread[maxThreads];
            _queue = new Queue<HttpListenerContext>();
            _stop = new ManualResetEvent(false);
            _ready = new ManualResetEvent(false);
            _listener = new HttpListener();
            _listenerThread = new Thread(HandleRequests);
        }

        public void Start(int port)
        {
            _listener.Prefixes.Add(String.Format(@"http://+:{0}/", port));
            _listener.Start();
            _listenerThread.Start();

            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(Worker);
                _workers[i].Start();
            }
        }

        public void Dispose()
        { Stop(); }

        public void Stop()
        {
            _stop.Set();
            _listenerThread.Join();
            foreach (Thread worker in _workers)
                worker.Join();
            _listener.Stop();
        }

        private void HandleRequests()
        {
            while (_listener.IsListening)
            {
                var context = _listener.BeginGetContext(ContextReady, null);

                if (0 == WaitHandle.WaitAny(new[] { _stop, context.AsyncWaitHandle }))
                    return;
            }
        }

        private void ContextReady(IAsyncResult ar)
        {
            try
            {
                lock (_queue)
                {
                    _queue.Enqueue(_listener.EndGetContext(ar));
                    _ready.Set();
                }
            }
            catch { return; }
        }

        private void Worker()
        {
            WaitHandle[] wait = new[] { _ready, _stop };
            while (0 == WaitHandle.WaitAny(wait))
            {
                HttpListenerContext context;
                lock (_queue)
                {
                    if (_queue.Count > 0)
                        context = _queue.Dequeue();
                    else
                    {
                        _ready.Reset();
                        continue;
                    }
                }

                try { ProcessRequest(context); }
                catch (Exception e) { Console.Error.WriteLine(e); }
            }
        }

        public event Action<HttpListenerContext> ProcessRequest;
    }

    class WebServer : HttpServer
    {
        public event EventHandler<string> ReceivedCommand;

        public WebServer()
            : base(1)
        {
            this.ProcessRequest += OnProcessRequest;
        }

        void OnProcessRequest(HttpListenerContext context)
        {
            if (!context.Request.Url.LocalPath.Equals("/"))
                return;

            context.Response.AddHeader("Access-Control-Allow-Origin", "*");

            if (context.Request.HttpMethod == "GET")
            {
                HandleGet(context);
            }

            context.Response.Close();
        }

        private void HandleGet(HttpListenerContext context)
        {
            var input = context.Request.Url.ToString();
            input = input.Substring(input.IndexOf("?") + 1);

            if (this.ReceivedCommand != null)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => this.ReceivedCommand(this, input)));
            }
        }
    }
}
