using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DSADCalib
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window
    {

        byte[] sendBuf;          // 送信バッファ   
        int sendByteLen;         //　送信データのバイト数

        byte[] rcvBuf;           // 受信バッファ
        int srcv_pt;             // 受信データ格納位置

        DateTime receiveDateTime;           // 受信完了日時

        DispatcherTimer SendIntervalTimer;  // タイマー　モニタ用　電文送信間隔   
        DispatcherTimer RcvWaitTimer;       // タイマー　受信待ち用 

        uint ad_data_max;                  // ADデータのサンプル数
        double[] ad_data0;                 // DSAD カウントデータ

        ScottPlot.Plottable.SignalPlot ad_signal_0;    //　DSAD カウントデータ 


        public MainWindow()
        {
            InitializeComponent();

            ConfSerial.serialPort = new SerialPort();    // シリアルポートのインスタンス生成

            ConfSerial.serialPort.DataReceived += new SerialDataReceivedEventHandler(DataReceivedHandler);  // データ受信時のイベント処理

            sendBuf = new byte[2048];     // 送信バッファ領域  serialPortのWriteBufferSize =2048 byte(デフォルト)
            rcvBuf = new byte[4096];      // 受信バッファ領域   SerialPort.ReadBufferSize = 4096 byte (デフォルト)

            
            SendIntervalTimer = new System.Windows.Threading.DispatcherTimer();　　// タイマーの生成(定周期モニタ用)
            SendIntervalTimer.Tick += new EventHandler(SendIntervalTimer_Tick);  // タイマーイベント
            SendIntervalTimer.Interval = new TimeSpan(0, 0, 0, 0, 2000);         // タイマーイベント発生間隔 2sec(コマンド送信周期)

            RcvWaitTimer = new System.Windows.Threading.DispatcherTimer();　 // タイマーの生成(受信待ちタイマ)
            RcvWaitTimer.Tick += new EventHandler(RcvWaitTimer_Tick);        // タイマーイベント
            RcvWaitTimer.Interval = new TimeSpan(0, 0, 0, 0, 3000);          // タイマーイベント発生間隔 (受信待ち時間)


            ad_data_max = 1000;             // A/D変換データのサンプル数
            ad_data0 = new double[ad_data_max];

            Chart_Ini_disp();

        }


        // グラフの初期表示
        private void Chart_Ini_disp()
        {

            for (int i = 0; i < ad_data_max; i++)
            {
                ad_data0[i] = i;
            }

            wpfPlot_AD_Trend.Refresh();    // データ変更後のリフレッシュ
            wpfPlot_AD_Histogram.Refresh();

            disp_ad_trend_graph();          // ADデータグラフの表示

            disp_ad_histo_gram();           // AD収集データ　ヒストグラムの表示
        }



        // モニタ用
        // 定周期にコマンドを送信する。
        private void SendIntervalTimer_Tick(object sender, EventArgs e)
        {
            bool fok = send_disp_data();       // データ送信
        }



        // 送信後、1000msec以内に受信文が得られないと、受信エラー
        //  
        private void RcvWaitTimer_Tick(object sender, EventArgs e)
        {

            RcvWaitTimer.Stop();        // 受信監視タイマの停止
            SendIntervalTimer.Stop();   // 定周期モニタ用タイマの停止

            StatusTextBlock.Text = "Receive time out";
        }

        private delegate void DelegateFn();

        // データ受信時のイベント処理
        private void DataReceivedHandler(object sender, SerialDataReceivedEventArgs e)
        {
            int id = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Console.WriteLine("DataReceivedHandlerのスレッドID : " + id);

            int rd_num = ConfSerial.serialPort.BytesToRead;       // 受信データ数

            ConfSerial.serialPort.Read(rcvBuf, srcv_pt, rd_num);   // 受信データの読み出し

            srcv_pt = srcv_pt + rd_num;     // 次回の保存位置

            int rcv_total_byte = 0;
            if (rcvBuf[0] == 0xc0)          // DSAD A/ D変換データ(N個)の収集開始コマンドのレスポンスの場合
            {
                rcv_total_byte = 4;
            }
            else if (rcvBuf[0] == 0xc1)     // DSAD A/ D変換データ(N個)の収集 状態読み出しコマンドのレスポンスの場合
            {
                rcv_total_byte = 4;
            }
            else if (rcvBuf[0] == 0xc2)     // DSAD A/ D変換データ(N個)の読み出しコマンドのレスポンスの場合
            {
                rcv_total_byte = 4004;
            }


            if (srcv_pt == rcv_total_byte)  // 最終データ受信済み (受信データは、52byte固定)
            {
                RcvWaitTimer.Stop();        // 受信監視タイマー　停止
                
                receiveDateTime = DateTime.Now;   // 受信完了時刻を得る

                Dispatcher.BeginInvoke(new DelegateFn(RcvMsgProc)); // Delegateを生成して、RcvMsgProcを開始   (表示は別スレッドのため)
            }

        }


        //  
        //  最終データ受信後の処理
        private void RcvMsgProc()
        {
           
            if (rcvBuf[0] == 0xc0)      // DSAD A/ D変換データ(N個)の収集開始コマンドのレスポンスの場合
            {
                Read_AD_Collect_Status();   // 状態読み出しコマンドの送信データの設定
             }

            else if (rcvBuf[0] == 0xc1)  // DSAD A/ D変換データ(N個)の収集 状態読み出しコマンドのレスポンスの場合
            {
                if ((rcvBuf[1] == 2))   //　データ収集完了の場合
                {
                    Read_AD_Collect_Data();     //  DSAD A/D変換データ(N個)の収集　データ読み出しコマンドの設定
                }
            }


            else if (rcvBuf[0] == 0xc2)     //  DSAD A/ D変換データ(N個)の読み出しコマンドのレスポンスの場合
            {
                SendIntervalTimer.Stop();   // 定周期モニタ用タイマの停止

                set_graph_data();    //  AD収集データを 受信データから取り出し、グラフ用の配列へコピー

                disp_ad_trend_graph();  // AD収集データ　トレンドグラフの表示

                disp_ad_histo_gram();   // AD収集データ　ヒストグラムの表示
            }

            if (ShowMessageCheckBox.IsChecked == true)      // 送受信文表示 ONの場合
            {
                rcvmsg_disp();              // 受信データの表示 
            }


        }



        //  AD収集データを 受信データから取り出し、グラフ用の配列へコピー
        // No. 受信データ :内容

        //  0: rcvBuf[0] : 0xc2 (コマンドに対するレスポンス)
        //     se_data[1] : dummy
        //     rcvBuf[2] : dummy
        //     rcvBuf[3] : dummy
        //  1: rcvBuf[4] : ad_ch0_data[0]の最下位バイト 
        //     rcvBuf[5] :
        //     rcvBuf[6] :    
        //     rcvBuf[7] : ad_ch0_data[0]の最上位バイト
        //  2: rcvBuf[8] : ad_ch0_data[1]の最下位バイト   
        //     rcvBuf[9] :
        //     rcvBuf[10] :    
        //     rcvBuf[11] : ad_ch0_data[1]の最上位バイト
        //          :
        //          :
        //          :
        //100: rcvBuf[400] : ad_ch0_data[99]の最下位バイト   
        //     rcvBuf[401] :
        //     rcvBuf[402] :    
        //     rcvBuf[403] : ad_ch0_data[99]の最上位バイト
        //          :
        //          :
        //4000: rcvBuf[4000]: sd_ch0_data[3999]の最下位バイト   
        //      rcvBuf[4001]:
        //      rcvBuf[4002]:
        //      rcvBuf[4003]: ad_ch0_data[3999]の最上位バイト
        //

        private void set_graph_data()
        {
            Int32    dsad_data;

             for ( int i = 0; i < ad_data_max; i++)
             {
                  dsad_data = BitConverter.ToInt32(rcvBuf, (i * 4) + 4);   // バイト型の配列データを、Int32型へ変換

                  ad_data0[i] = (float)dsad_data;                      // グラフ表示用データはfloat型
             }

             wpfPlot_AD_Trend.Refresh();   // データ変更後のリフレッシュ
             wpfPlot_AD_Histogram.Refresh();

        }





        // AD収集データ　トレンドグラフの表示
        //
        private void disp_ad_trend_graph()
        {
            wpfPlot_AD_Trend.Plot.Clear();        /// グラフ表示のクリア

            wpfPlot_AD_Trend.Configuration.Pan = true;               // パン(グラフの移動)可
            wpfPlot_AD_Trend.Configuration.ScrollWheelZoom = true;   // ズーム(グラフの拡大、縮小)可

            wpfPlot_AD_Trend.Plot.AxisAuto();     // オートスケール

            wpfPlot_AD_Trend.Plot.XAxis.Ticks(true, false, true);        // X軸の大きい目盛り=表示, X軸の小さい目盛り=非表示, X軸の目盛りのラベル=表示
            wpfPlot_AD_Trend.Plot.XAxis.TickLabelStyle(fontSize: 12);     // X軸   ラベルのフォントサイズ変更  :
            wpfPlot_AD_Trend.Plot.XLabel("time (  x 20msec )");                         // X軸全体のラベル


            wpfPlot_AD_Trend.Plot.YAxis.TickLabelStyle(fontSize: 12);     // Y軸   ラベルのフォントサイズ変更  :
            wpfPlot_AD_Trend.Plot.YAxis.Label(label: "DSAD data", color: System.Drawing.Color.Black);                       // Y軸全体のラベル 
       
            ad_signal_0 = wpfPlot_AD_Trend.Plot.AddSignal(ad_data0, color: System.Drawing.Color.LightBlue); // プロット plot the data array only once
 
            wpfPlot_AD_Trend.Render();    //  　グラフの更新

        }


        // AD収集データ　ヒストグラムの表示
        // https://scottplot.net/cookbook/4.1/category/statistics/#histogram

        private void disp_ad_histo_gram()
        {

            wpfPlot_AD_Histogram.Plot.Clear();  //グラフ表示のクリア


            double ad_min = ad_data0.Min();   // 最小値　LINQの使用
            double ad_max = ad_data0.Max();   // 最大値

            int binCount = 1000;            // 同一の値とする数値の範囲 (ヒストグラムの階級幅)

            // create a histogram
            (double[] counts, double[] binEdges) = ScottPlot.Statistics.Common.Histogram(ad_data0, min:ad_min-1,max:ad_max+1, binCount);
            double[] leftEdges = binEdges.Take(binEdges.Length - 1).ToArray();


            // display the histogram counts as a bar plot
            var bar = wpfPlot_AD_Histogram.Plot.AddBar(values: counts, positions: leftEdges,color:System.Drawing.Color.LightBlue);
            // bar.BarWidth = 1;
            bar.BarWidth = binCount;

            wpfPlot_AD_Histogram.Plot.XAxis.Ticks(true, false, true);        // X軸の大きい目盛り=表示, X軸の小さい目盛り=非表示, X軸の目盛りのラベル=表示
            wpfPlot_AD_Histogram.Plot.XAxis.TickLabelStyle(fontSize: 12);    // X軸   ラベルのフォントサイズ変更  :
            wpfPlot_AD_Histogram.Plot.XLabel("DSAD data");                   // X軸全体のラベル
            wpfPlot_AD_Histogram.Plot.XAxis.MinimumTickSpacing(1);           // 目盛りの最小値 = 1 (ズームしてもこれ以上小さくならない)

            wpfPlot_AD_Histogram.Plot.YAxis.Ticks(true,false,true);          // Y軸の大きい目盛り=表示, Y軸の小さい目盛り=非表示, Y軸の目盛りのラベル=表示
            wpfPlot_AD_Histogram.Plot.YAxis.TickLabelStyle(fontSize: 12);     // Y軸   ラベルのフォントサイズ変更  :
            wpfPlot_AD_Histogram.Plot.YAxis.Label(label: "Occurrence", color: System.Drawing.Color.Black);           // Y軸全体のラベル 
            wpfPlot_AD_Histogram.Plot.YAxis.MinimumTickSpacing(1);           // 目盛りの最小値 = 1 

            var stats = new ScottPlot.Statistics.BasicStats(ad_data0);       // 統計情報を得る

            int mean = (int)stats.Mean;         //　平均値
            int stdev = (int)stats.StDev;       // 標準偏差                                   　
            int min = (int)stats.Min;           // 最小値
            int max = (int)stats.Max;           // 最大値

            DataMeanTextBox.Text = mean.ToString();                  //平均値の表示
            DataMeanHexTextBox.Text = "(" + "0x" + mean.ToString("x8") + ")"; // Hex表示

            DataStdDevTextBox.Text = stdev.ToString();                   //標準偏差
            
            DataMinTextBox.Text = min.ToString("");                        // 最小値
            DataMinHexTextBox.Text = "(" + "0x" + min.ToString("x8") + ")";
            
            DataMaxTextBox.Text = max.ToString("");                        // 最大値
            DataMaxHexTextBox.Text = "(" + "0x" + max.ToString("x8") + ")";

            wpfPlot_AD_Histogram.Render();     //  　グラフの更新
        }

       

        // 受信データの表示
        //
        private void rcvmsg_disp()
        {
            string rcv_str = "";

            for (int i = 0; i < srcv_pt; i++)   // 表示用の文字列作成
            {
                    rcv_str = rcv_str + rcvBuf[i].ToString("X2") + " ";
            }
                                                // 受信文と時刻の表示
            RcvmsgTextBlock.Text +=  rcv_str + "(" + receiveDateTime.ToString("HH:mm:ss.fff") + ")(" + srcv_pt.ToString() + " bytes )" + "\r\n";

            RcvmsgScrol.ScrollToBottom();           // 一番下までスクロール  
           
        }


        // DSAD A/D変換データ(N個)の収集開始コマンド
        private void Start_AD_Button_Click(object sender, RoutedEventArgs e)
        {
            wpfPlot_AD_Trend.Plot.Clear();           //  表示データのクリア
            wpfPlot_AD_Histogram.Plot.Clear();       //  表示データのクリア

            wpfPlot_AD_Trend.Refresh();              // データ変更後のリフレッシュ
            wpfPlot_AD_Histogram.Refresh();


            sendBuf[0] = 0x40;     // 送信コマンド  
            sendBuf[1] = 0;
            sendBuf[2] = 0;
            sendBuf[3] = 0;

            sendByteLen = 4;                   // 送信バイト数

            SendIntervalTimer.Start();   // 定周期　送信用タイマの開始
 
        }

        // コマンド送信の停止
        private void Stop_AD_Button_Click(object sender, RoutedEventArgs e)
        {
            SendIntervalTimer.Stop();   // 定周期　送信用タイマの停止
        }




        //  DSAD A/D変換データ(N個)の収集　状態読み出しコマンドの設定
        // 
        private void Read_AD_Collect_Status()
        {

            sendBuf[0] = 0x41;      // DSAD A/D変換データ(N個)の収集　状態読み出しコマンド
            sendBuf[1] = 0;
            sendBuf[2] = 0;
            sendBuf[3] = 0;

            sendByteLen = 4;                   // 送信バイト数
        }


        //  DSAD A/D変換データ(N個)の収集　データ読み出しコマンドの設定
        // 
        private void Read_AD_Collect_Data()
        {

            sendBuf[0] = 0x42;      // DSAD A/D変換データ(N個)の収集　データ読み出しコマンド
            sendBuf[1] = 0;
            sendBuf[2] = 0;
            sendBuf[3] = 0;

            sendByteLen = 4;                   // 送信バイト数
        }



        //  送信と送信データの表示
        // sendBuf[]のデータを、sendByteLenバイト　送信する
        // 戻り値  送信成功時: true
        //         送信失敗時: false

        private bool send_disp_data()
        {
            if (ConfSerial.serialPort.IsOpen == true)
            {
                srcv_pt = 0;                   // 受信データ格納位置クリア

                ConfSerial.serialPort.Write(sendBuf, 0, sendByteLen);     // データ送信

                if (ShowMessageCheckBox.IsChecked == true)
                {  // 送受信文表示 ONの場合  
                    sendmsg_disp();          // 送信データの表示
                }

                RcvWaitTimer.Start();        // 受信監視タイマー　開始

                StatusTextBlock.Text = "";
                return true;
            }

            else
            {
                StatusTextBlock.Text = "Comm port closed !";
                SendIntervalTimer.Stop();
                return false;
            }

        }


        // 送信データの表示
        private void sendmsg_disp()
        {
           for (int i = 0; i < sendByteLen; i++)
           {
                SendmsgTextBlock.Text +=  sendBuf[i].ToString("X2") + " ";
           }

           SendmsgTextBlock.Text += "(" + DateTime.Now.ToString("HH:mm:ss.fff") + ")" + "\r\n";

           SendmsgScrol.ScrollToBottom();   // 一番下までスクロール

        }

        //　通信条件の設定 ダイアログを開く
        //  
        private void Serial_Button_Click(object sender, RoutedEventArgs e)
        {
            new ConfSerial().ShowDialog();
        }

        // チェックボックス show が Uncheckになった時 (checkからuncheckになった時)
        // 送受信データの表示クリア
        private void ShowMessageCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SendmsgTextBlock.Text = "";
            RcvmsgTextBlock.Text = "";
        }

        // A/D変換データの保存
        //  先頭行はメモ欄の文字列、2行目は保存日時と平均値等、
        //  3行目からA/D変換データを1000データ(10個を1行として100行)ファイルへ保存
        //  書式: 
        // 行番号:    内容
        //  1 : メモ欄  例:入力電圧= 1.0[V]  ,室温=20℃
        //  2 : 2020年12月27日 10;25;00 ,平均値=xx  , 標準偏差=xx, 最小値=xx, 最大値=xx
        //  3 : ad_data0[0],ad_data0[1],   ........................ ad_data[9]
        //  4 : ad_data0[10],              .........................ad_data[19]
        //    :
        // 102: ad_data0[990],              .........................ad_data[999]

        private void Save_Button_Click(object sender, RoutedEventArgs e)
        {
            string path;
            string str_one_line;

            SaveFileDialog sfd = new SaveFileDialog();           //　SaveFileDialogクラスのインスタンスを作成 

            sfd.FileName = "adcnt.csv";                              //「ファイル名」で表示される文字列を指定する

            sfd.Title = "保存先のファイルを選択してください。";        //タイトルを設定する 

            sfd.RestoreDirectory = true;                 //ダイアログボックスを閉じる前に現在のディレクトリを復元するようにする

            if (sfd.ShowDialog() == true)            //ダイアログを表示する
            {
                path = sfd.FileName;

                try
                {
                    System.IO.StreamWriter sw = new System.IO.StreamWriter(path, false, System.Text.Encoding.Default);

                    str_one_line = DataMemoTextBox.Text; // メモ欄
                    sw.WriteLine(str_one_line);         // 1行保存


                    str_one_line = DateTime.Now.ToString("F") + ",";
                    str_one_line = str_one_line + "平均値=" + DataMeanTextBox.Text + ",";
                    str_one_line = str_one_line + "標準偏差=" + DataStdDevTextBox.Text + ",";
                    str_one_line = str_one_line + "最小値=" + DataMinTextBox.Text + ",";
                    str_one_line = str_one_line + "最大値=" + DataMaxTextBox.Text;

                    sw.WriteLine(str_one_line);         // 1行保存


                    for (int i = 0; i < ad_data_max; i=i+10)         // ad_data0[0]～ ad_data0[999]の内容を保存
                    {
                        string st_dt0 = ad_data0[i].ToString();       // 文字型に変換 
                        string st_dt1 = ad_data0[i+1].ToString();       
                        string st_dt2 = ad_data0[i+2].ToString();       
                        string st_dt3 = ad_data0[i+3].ToString();
                        string st_dt4 = ad_data0[i+4].ToString();

                        string st_dt5 = ad_data0[i+5].ToString();       
                        string st_dt6 = ad_data0[i+6].ToString();
                        string st_dt7 = ad_data0[i+7].ToString();
                        string st_dt8 = ad_data0[i+8].ToString();
                        string st_dt9 = ad_data0[i+9].ToString();

                        str_one_line = st_dt0 + "," + st_dt1 + "," + st_dt2 + "," + st_dt3 + "," + st_dt4 + ",";
                        str_one_line = str_one_line + st_dt5 + "," + st_dt6 + "," + st_dt7 + "," + st_dt8 + "," + st_dt9;

                        sw.WriteLine(str_one_line);         // 1行保存
                    }

                    sw.Close();
                }

                catch (System.Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }

            }
        }
        
        // A/D変換データファイルのオープンとグラフ表示
        // 通信ポート
        private void Open_Button_Click(object sender, RoutedEventArgs e)
        {

            var dialog = new OpenFileDialog();   // ダイアログのインスタンスを生成

            dialog.Filter = "csvファイル (*.csv)|*.csv|全てのファイル (*.*)|*.*";  //  // ファイルの種類を設定

            dialog.RestoreDirectory = true;                 //ダイアログボックスを閉じる前に現在のディレクトリを復元するようにする


            if (dialog.ShowDialog() == false)     // ダイアログを表示する
            {
                return;                          // キャンセルの場合、リターン
            }


            try
            {

                wpfPlot_AD_Trend.Plot.Clear();        /// グラフ表示のクリア
                wpfPlot_AD_Histogram.Plot.Clear();

                StreamReader sr = new StreamReader(dialog.FileName, Encoding.GetEncoding("SHIFT_JIS"));    //  CSVファイルを読みだし

                FileNameTextBox.Text = dialog.FileName;                // ファイル名の表示

                DataMemoTextBox.Text = sr.ReadLine();        // 先頭行の読み出して、Memo欄に表示

                sr.ReadLine();              // 2行目の読み飛ばし (2行目は、日時、平均値、標準偏差、最小値、最大値のため)


                for (int i = 0; i < ad_data_max; i = i + 10)         // ad_data0[0]～ ad_data0[999]の内容を保存
                {
                    string line = sr.ReadLine();        // 1行の読み出し

                    string[] items = line.Split(',');       // 1行を、,(カンマ)毎に items[]に格納 

                    double.TryParse(items[0], out ad_data0[i]);         // 文字列を double型へ変換
                    double.TryParse(items[1], out ad_data0[i + 1]);
                    double.TryParse(items[2], out ad_data0[i + 2]);
                    double.TryParse(items[3], out ad_data0[i + 3]);
                    double.TryParse(items[4], out ad_data0[i + 4]);
                    double.TryParse(items[5], out ad_data0[i + 5]);
                    double.TryParse(items[6], out ad_data0[i + 6]);
                    double.TryParse(items[7], out ad_data0[i + 7]);
                    double.TryParse(items[8], out ad_data0[i + 8]);
                    double.TryParse(items[9], out ad_data0[i + 9]);
                }

                wpfPlot_AD_Trend.Refresh();   // データ変更後のリフレッシュ
                wpfPlot_AD_Histogram.Refresh();

                disp_ad_trend_graph();  // AD収集データ　トレンドグラフの表示
                disp_ad_histo_gram();   // AD収集データ　ヒストグラムの表示

            }


            catch (Exception ex) when (ex is IOException || ex is IndexOutOfRangeException)
            {

                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);

            }
        }

      
    }
}
