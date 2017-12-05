﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using BaiduCloudSync;
using GlobalUtil;
using GlobalUtil.NetUtils;
using System.IO;
using System.Text.RegularExpressions;

namespace BaiduCloudConsole
{
    class Program
    {
        private static RemoteFileCacher _remote_file_cacher;
        private static LocalFileCacher _local_file_cacher;
        private static KeyManager _key_manager;

        private static string _version = "1.0.0 pre-alpha";
        private static void Main(string[] args)
        {
            if (!Directory.Exists("data"))
                Directory.CreateDirectory("data");
            NetStream.LoadCookie("data/cookie.dat");
            _key_manager = new KeyManager();
            if (File.Exists("data/rsa_key.pem"))
                _key_manager.LoadKey("data/rsa_key.pem");
            if (File.Exists("data/aes_key.dat"))
                _key_manager.LoadKey("data/aes_key.dat");

            Console.WriteLine("BaiduCloudSync 控制台模式");
            Console.WriteLine("Version: {0}", _version);
            Console.WriteLine("");

            try
            {
                if (args.Length == 0)
                    _print_no_arg();
                else
                {
                    _check_and_login();

                    _exec_command(args);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            if (!Directory.Exists("data"))
                Directory.CreateDirectory("data");
            NetStream.SaveCookie("data/cookie.dat");
            _key_manager.SaveKey("data/rsa_key.pem", true);
            _key_manager.SaveKey("data/aes_key.dat", false);
            //Console.ReadKey();
        }
        //检验登陆状态，如未登陆则登陆
        private static void _check_and_login()
        {
            _remote_file_cacher = new RemoteFileCacher();
            _local_file_cacher = new LocalFileCacher();
            var account_count = NetStream.DefaultCookieContainer.Keys.Count;
            if (account_count == 0)
            {
                Console.WriteLine("未检测到登陆信息，请登陆 [L] 或者通过cookie传递账号信息 [C]");
                Console.Write("输入 [L] 或 [C] > ");
                var str = Console.ReadLine().ToLower();
                var oauth = new BaiduOAuth();
                if (str == "l")
                {
                    Console.Write("输入账号 > ");
                    var username = Console.ReadLine();
                    Console.Write("输入密码 > ");
                    string password = string.Empty;
                    while (true)
                    {
                        var key_data = Console.ReadKey(true);
                        if (key_data.Key == ConsoleKey.Enter)
                        {
                            Console.WriteLine();
                            break;
                        }
                        else if (key_data.Key == ConsoleKey.Backspace)
                        {
                            if (password.Length > 0)
                            {
                                password = password.Substring(0, password.Length - 1);
                                Console.Write("\b \b");
                            }
                        }
                        else
                        {
                            if (key_data.KeyChar > 0)
                            {
                                password += key_data.KeyChar;
                                Console.Write("*");
                            }
                        }
                    }

                    bool captcha_required = false;
                    string captcha = string.Empty;

                    oauth.LoginCaptchaRequired += delegate
                    {
                        captcha_required = true;
                    };

                    bool login_suc = false;
                    while (login_suc == false)
                    {
                        if (captcha_required)
                            oauth.SetVerifyCode(captcha);

                        Console.WriteLine("开始登陆");
                        captcha_required = false;
                        login_suc = oauth.Login(username, password);
                        if (login_suc)
                            Console.WriteLine("登陆成功");
                        else
                            Console.WriteLine("登陆失败: [{0}]: {1}", oauth.GetLastFailedCode, oauth.GetLastFailedReason);

                        if (captcha_required)
                        {
                            Console.WriteLine("下载验证码图片...");
                            var img = oauth.GetCaptcha();
                            if (!Directory.Exists("cache"))
                                Directory.CreateDirectory("cache");
                            img.Save("cache/captcha.bmp", System.Drawing.Imaging.ImageFormat.Bmp);
                            System.Diagnostics.Process.Start(Path.Combine(Environment.CurrentDirectory, "cache", "captcha.bmp"));
                            Console.Write("请输入该验证码 > ");
                            captcha = Console.ReadLine();
                        }
                    }
                }
                else if (str == "c")
                {
                    //login by cookie
                    Console.Write("请输入cookie.txt的文件路径 > ");
                    var path = Console.ReadLine();
                    if (string.IsNullOrEmpty(path))
                    {
                        Console.WriteLine("错误：空路径");
                        Environment.Exit(0);
                    }
                    else if (File.Exists(path) == false)
                    {
                        Console.WriteLine("错误：文件不存在");
                        Environment.Exit(0);
                    }
                    _parse_cookie_txt(path);
                }
                else
                {
                    Console.WriteLine("非法输入");
                    Environment.Exit(0);
                }
                NetStream.SaveCookie("data/cookie.dat");
                _remote_file_cacher.AddAccount(new BaiduPCS(oauth));
                Console.WriteLine("欢迎回来，" + oauth.NickName);
            }

        }
        private static void _parse_cookie_txt(string _path)
        {
            var text = File.ReadAllLines(_path);
            var key = "default";
            if (NetStream.DefaultCookieContainer.Keys.Count > 0)
                key = NetStream.DefaultCookieContainer.Keys.First();
            else
                NetStream.DefaultCookieContainer.Add(key, new System.Net.CookieContainer());

            foreach (var item in text)
            {
                var reg = Regex.Match(item, @"^(?<domain>.+?)\s+(?<flag>(TRUE|FALSE))\s+(?<path>.+?)\s+(?<secure>(TRUE|FALSE))\s+(?<expiration>\d+)\s+(?<name>.+?)\s+(?<value>.+?)$");
                if (reg.Success)
                {
                    var domain = reg.Result("${domain}");
                    var flag = reg.Result("${flag}") == "TRUE";
                    var path = reg.Result("${path}");
                    var secure = reg.Result("${secure}") == "TRUE";
                    var expiration = long.Parse(reg.Result("${expiration}"));
                    var name = reg.Result("${name}");
                    var value = reg.Result("${value}");

                    var cookie = new System.Net.Cookie(name, value, path, domain);
                    cookie.Secure = secure;

                    NetStream.DefaultCookieContainer[key].Add(cookie);
                }
            }

            var valid_result = NetStream.DefaultCookieContainer[key].GetCookies(new Uri("https://passport.baidu.com/"));
            if (valid_result.Count == 0)
            {
                Console.WriteLine("文件数据未含有登陆信息");
                Environment.Exit(0);
            }
        }
        private static void _print_no_arg()
        {
            Console.WriteLine("输入 -H 或者 --help 获取更多帮助");
        }
        private static void _print_help()
        {
            _print_no_arg();
            Console.WriteLine();
            Console.WriteLine("使用命令行:");
            Console.WriteLine("BaiduCloudConsole --[函数名] 参数");
            //Console.WriteLine();
            //Console.WriteLine("*** 账号相关 ***");
            //Console.WriteLine("--add-account --username [用户名] --password [密码]");
            //Console.WriteLine("\t添加指定的百度账号/密码，使用OAuth登陆");
            //Console.WriteLine("--add-account --cookie [cookie.txt]");
            //Console.WriteLine("\t添加指定的百度账号，使用cookie的文件格式为Netscape cookie.txt，兼容curl和wget的");
            //Console.WriteLine("--list-account");
            //Console.WriteLine("\t列出所有已保存的账号");
            //Console.WriteLine("--delete-account --username [用户名]");
            //Console.WriteLine("\t删除指定用户名的登陆信息");
            Console.WriteLine("*** 文件操作 ***");
            Console.WriteLine("-L | --list [网盘文件路径] [--order [排序:name|size|time] --page [页数] --count [每页显示数量] --desc]");
            Console.WriteLine("\t列出文件夹下所有文件，参数page从1开始");
            Console.WriteLine();
            Console.WriteLine("*** 文件传输 ***");
            Console.WriteLine("-D | --download [网盘文件路径] [本地文件路径] [--threads [下载线程数] --speed [限速(KB/s)]]");
            Console.WriteLine("\t下载文件，可选选项：下载线程数（默认96），限速（默认0，即无限速）");
            Console.WriteLine("-U | --upload [本地文件路径] [网盘文件路径] [--threads [上传线程数] --speed [限速(KB/s)] --encrypt]");
            Console.WriteLine("\t上传文件，可选选项：上传线程数（默认4），限速（默认0，即无限速）\r\n\t有encrypt选项开启文件加密，密钥见加密部分");
            Console.WriteLine();
            Console.WriteLine("*** 加密部分 ***");
            Console.WriteLine("--show-key");
            Console.WriteLine("\t输出目前的密钥信息");
            Console.WriteLine("--load-key [文件路径] [[-F | --force]]");
            Console.WriteLine("\t从指定的位置读取密钥到程序中，如密钥已存在，需要force进行强制覆盖，注：RSA密钥文件后缀必须为pem");
            Console.WriteLine("--save-key [文件路径]");
            Console.WriteLine("\t输出当前密钥到指定位置中\r\n\t注：RSA密钥的文件后缀为pem，如非pem，则会自动添加后缀");
            Console.WriteLine("--create-key [[密钥类型: rsa|aes]] [-F | --force]");
            Console.WriteLine("\t生成密钥，如密钥已存在，需要force进行强制覆盖，如不指定密钥类型，默认为rsa");
            Console.WriteLine("--delete-key [密钥类型: rsa|aes]");
            Console.WriteLine("\t删除密钥，利用该密钥加密的所有文件将会无法解密");
            Console.WriteLine();
        }
        private static void _exec_download(string[] cmd)
        {
            if (cmd.Length < 3)
            {
                Console.WriteLine("参数不足");
                return;
            }

            var remote_path = cmd[1];
            var local_path = cmd[2];

            var max_thread = Downloader.DEFAULT_MAX_THREAD;
            var speed_limit = 0;

            if (cmd.Length > 3)
            {
                int index = 3;
                while (index < cmd.Length)
                    switch (cmd[index])
                    {
                        case "--threads":
                            //max_thread = int.Parse(cmd[index + 1]);
                            if (int.TryParse(cmd[index + 1], out max_thread) == false)
                            {
                                Console.WriteLine("线程数 {0} 无法转换为整型", cmd[index + 1]);
                                return;
                            }
                            index += 2;
                            break;
                        case "--speed":
                            double temp_speed;
                            if (double.TryParse(cmd[index + 1], out temp_speed) == false)
                            {
                                Console.WriteLine("限速 {0} 无法转换为浮点数", cmd[index + 1]);
                                return;
                            }
                            speed_limit = (int)temp_speed * 1024;
                            index += 2;
                            break;

                        default:
                            Console.WriteLine("未知参数：" + cmd[index]);
                            return;
                    }
            }

            Console.WriteLine("等待文件信息与服务器同步结束");
            var rst_event = new ManualResetEventSlim();
            bool wait_diff_complete = true;
            _remote_file_cacher.FileDiffFinish += delegate
            {
                if (wait_diff_complete)
                {
                    rst_event.Set();
                    wait_diff_complete = false;
                }
            };
            rst_event.Wait();

            Console.WriteLine("从SQL读取文件信息……");
            var parent_dir_path = remote_path.Substring(0, remote_path.LastIndexOf('/'));
            if (string.IsNullOrEmpty(parent_dir_path)) parent_dir_path = "/"; //根目录修正

            rst_event.Reset();
            var file_list = new List<ObjectMetadata>();
            _remote_file_cacher.GetFileListAsync(parent_dir_path, _file_list_callback, state: new _temp_callback_state { reset = rst_event, list = file_list, page = 1, path = parent_dir_path });

            rst_event.Wait();
            var remote_file_info = file_list.Find(o => o.Path == remote_path);

            //开始下载
            var downloader = new Downloader(_remote_file_cacher, remote_file_info, local_path, max_thread, speed_limit, _key_manager);
            downloader.Start();
            downloader.TaskError += delegate
            {
                Console.WriteLine();
                Console.WriteLine("下载发生错误！");
            };
            bool decrypt_started = false, decrypt_response = false;
            downloader.DecryptStarted += delegate
            {
                decrypt_started = true;
            };
            Console.WriteLine("预分配硬盘空间……");
            Downloader.State stat;
            do
            {
                stat = downloader.TaskState;
                long finished_size = downloader.DownloadedSize;
                long total_size = downloader.Size;
                double rate = 100.0 * finished_size / total_size;

                //进度条长度
                var bar_length = Math.Max(0, Console.WindowWidth - 45);
                var bar = new string('.', bar_length);
                var f_finished_bar = rate / 100 * bar_length;
                int i_finished_bar = (int)Math.Floor(f_finished_bar);
                var finished_bar = new string('=', i_finished_bar);
                if (bar_length != i_finished_bar)
                    bar = finished_bar + ">" + bar.Substring(0, bar_length - i_finished_bar - 1);
                else
                    bar = finished_bar;

                var size_info = string.Format("{0,-17}", _format_bytes(finished_size) + "/" + _format_bytes(total_size));

                var speed_info = new string(' ', 10);
                if (!decrypt_started)
                    speed_info = string.Format("{0,-10}", _format_bytes((long)downloader.AverageSpeed5s) + "/s");

                Console.Write("\r[" + rate.ToString("#0.0") + "%] [" + bar + "] " + size_info + " " + speed_info);
                //if (decrypt_started && !decrypt_response)
                //{
                //    decrypt_response = true;
                //    bar = new string('=', bar_length);
                //    size_info = new string(' ', 17);
                //    size_info = _format_bytes(total_size) + "/" + _format_bytes(total_size) + size_info;
                //    size_info = size_info.Substring(0, 17);
                //    speed_info = new string(' ', 10);
                //    Console.Write("\r[" + rate.ToString("#0.0") + "%] [" + bar + "] " + size_info + " " + speed_info);

                //    Console.WriteLine();
                //    Console.WriteLine("解密文件……");
                //}
                Thread.Sleep(100);
            } while (stat != Downloader.State.FINISHED);
            Console.WriteLine();
            Console.WriteLine("下载完成");
        }
        private static string _format_bytes(long b)
        {
            if (b < 0x400)
                return b.ToString() + "B";
            else if (b < 0x100000)
                return ((double)b / 0x400).ToString("0.0") + "KB";
            else if (b < 0x40000000)
                return ((double)b / 0x100000).ToString("0.0") + "MB";
            else if (b < 0x10000000000)
                return ((double)b / 0x40000000).ToString("0.0") + "GB";
            else
                return ((double)b / 0x10000000000).ToString("0.0") + "TB";
        }
        private struct _temp_callback_state
        {
            public ManualResetEventSlim reset;
            public List<ObjectMetadata> list;
            public int page;
            public string path;
        }
        private static void _file_list_callback(bool suc, ObjectMetadata[] data, object state)
        {
            var stat = (_temp_callback_state)state;
            var rst_event = stat.reset;
            var file_list = stat.list;
            var page = stat.page;
            var path = stat.path;

            if (!suc)
            {
                rst_event.Set();
                return;
            }

            file_list.AddRange(data);
            if (data.Length == 1000)
            {
                stat.page++;
                _remote_file_cacher.GetFileListAsync(path, _file_list_callback, page: page + 1, state: stat);
            }
            else
            {
                rst_event.Set();
            }
        }

        private static void _exec_upload(string[] cmd)
        {

        }
        private static void _exec_list(string[] cmd)
        {
            if (cmd.Length < 2)
            {
                Console.WriteLine("参数不足");
                return;
            }
            var path = cmd[1];
            BaiduPCS.FileOrder order = BaiduPCS.FileOrder.name;
            var page = 1;
            var count = 200;
            bool asc = true;
            if (cmd.Length > 2)
            {
                int index = 2;
                while (index < cmd.Length)
                {
                    switch (cmd[index])
                    {
                        case "--order":
                            if (cmd[index + 1] == "name")
                                order = BaiduPCS.FileOrder.name;
                            else if (cmd[index + 1] == "size")
                                order = BaiduPCS.FileOrder.size;
                            else if (cmd[index + 1] == "time")
                                order = BaiduPCS.FileOrder.time;
                            else
                            {
                                Console.WriteLine("无效的排序依据：" + cmd[index + 1]);
                                return;
                            }
                            index += 2;
                            break;
                        case "--page":
                            page = int.Parse(cmd[index + 1]);
                            index += 2;
                            break;
                        case "--count":
                            count = int.Parse(cmd[index + 1]);
                            index += 2;
                            break;
                        case "--desc":
                            asc = false;
                            break;
                        default:
                            break;
                    }
                }
            }

            var rst_event = new ManualResetEventSlim();
            ObjectMetadata[] files = null;
            _remote_file_cacher.GetFileListAsync(path, (suc, data, state) =>
            {
                if (suc)
                {
                    files = data;
                    rst_event.Set();
                }
                else
                {
                    Console.WriteLine("获取失败");
                }
            }, order: order, asc: asc, page: page, size: count);

            rst_event.Wait();
            if (files != null)
            {
                Console.WriteLine("{0} 的文件信息: ", path);
                var padding = new string(' ', Console.WindowWidth);
                //40,15,18,18
                var head = ("文件名" + padding).Substring(0, 37) + ("大小" + padding).Substring(0, 13) + ("创建时间" + padding).Substring(0, 14) + ("修改时间" + padding).Substring(0, 14);
                Console.WriteLine(head);
                foreach (var file in files)
                {
                    var str_filename = file.ServerFileName;
                    var len_filename = Encoding.Default.GetByteCount(str_filename);
                    if (len_filename > 40)
                    {
                        while (len_filename > 36)
                        {
                            str_filename = str_filename.Substring(0, str_filename.Length - 1);
                            len_filename = Encoding.Default.GetByteCount(str_filename);
                        }
                        if (len_filename == 36)
                            str_filename += "... ";
                        else
                            str_filename += " ... ";
                    }
                    else
                        str_filename = string.Format("{0,-" + (40 + str_filename.Length - len_filename) + "}", str_filename);

                    Console.Write(str_filename);

                    var size = string.Format("{0,-15}", file.IsDir ? "<DIR>" : _format_bytes((long)file.Size));
                    Console.Write(size);

                    var ctime = string.Format("{0,-18}", util.FromUnixTimestamp(file.ServerCTime).ToString("yyyy-MM-dd HH:mm"));
                    var mtime = string.Format("{0,-18}", util.FromUnixTimestamp(file.ServerMTime).ToString("yyyy-MM-dd HH:mm"));
                    Console.WriteLine(ctime + mtime);
                }
            }
        }
        private static void _exec_show_key(string[] cmd)
        {
            if (cmd.Length != 1)
            {
                Console.WriteLine("参数过多");
                return;
            }
            Console.WriteLine("当前的RSA密钥信息：");
            if (_key_manager.HasRsaKey)
            {
                var rsa = new System.Security.Cryptography.RSACryptoServiceProvider();
                rsa.ImportCspBlob(_key_manager.RSAPrivateKey);
                var bit_length = rsa.KeySize;
                Console.WriteLine("\t{0}位RSA密钥", bit_length);
                Console.WriteLine("\t密钥特征码：{0}", util.Hex(MD5.ComputeHash(_key_manager.RSAPrivateKey, 0, _key_manager.RSAPrivateKey.Length)));
            }
            else
            {
                Console.WriteLine("\t无RSA密钥信息");
            }
            Console.WriteLine();
            Console.WriteLine("当前的AES密钥信息：");
            if (_key_manager.HasAesKey)
            {
                Console.WriteLine("\tAES密钥：{0}", util.Hex(_key_manager.AESKey));
                Console.WriteLine("\tAES初始向量：{0}", util.Hex(_key_manager.AESIV));
            }
            else
            {
                Console.WriteLine("\t无AES密钥信息");
            }
        }
        private static void _exec_load_key(string[] cmd)
        {
            if (cmd.Length < 2)
            {
                Console.WriteLine("参数过少");
                return;
            }
            if (cmd.Length > 3)
            {
                Console.WriteLine("参数过多");
                return;
            }
            var path = cmd[1];
            if (!File.Exists(path))
            {
                Console.WriteLine("文件不存在");
                return;
            }
            bool enable_force = false;
            if (cmd.Length == 3)
                if (cmd[2] == "-F" || cmd[2] == "--force")
                    enable_force = true;
                else
                {
                    Console.WriteLine("无效参数 {0}", cmd[2]);
                    return;
                }

            if (path.EndsWith(".pem") && _key_manager.HasRsaKey)
            {
                if (enable_force)
                    _key_manager.LoadKey(path);
                else
                {
                    Console.WriteLine("RSA密钥已存在，如需覆盖请使用-F参数，或者调用--delete-key删除");
                    return;
                }
            }
            else if (_key_manager.HasAesKey)
            {
                if (enable_force)
                    _key_manager.LoadKey(path);
                else
                {
                    Console.WriteLine("AES密钥已存在，如需覆盖请使用-F参数，或者调用--delete-key删除");
                    return;
                }
            }
            else
                return;

            Console.WriteLine("已读取密钥文件 {0}", path);
        }
        private static void _exec_save_key(string[] cmd)
        {
            if (cmd.Length < 2)
            {
                Console.WriteLine("参数过少");
                return;
            }
            if (cmd.Length > 3)
            {
                Console.WriteLine("参数过多");
                return;
            }
            var path = cmd[1];
            if (path.EndsWith(".pem") && _key_manager.HasRsaKey)
                _key_manager.SaveKey(path, true);
            else if (_key_manager.HasAesKey)
                _key_manager.SaveKey(path, false);
            else
            {
                if (path.EndsWith(".pem"))
                    Console.WriteLine("无RSA密钥信息，保存失败");
                else
                    Console.WriteLine("无AES密钥信息，保存失败");
                return;
            }

            Console.WriteLine("文件已保存到 {0} 中", path);
        }
        private static void _exec_create_key(string[] cmd)
        {
            bool create_rsa = true;
            bool enable_force = false;
            bool has_key_specified = false;
            int index = 1;
            while (index < cmd.Length)
            {
                switch (cmd[index])
                {
                    case "-F":
                    case "--force":
                        enable_force = true;
                        index++;
                        break;
                    case "aes":
                        if (has_key_specified)
                        {
                            Console.WriteLine("多次指定密钥类型，操作无效");
                            return;
                        }
                        else
                        {
                            has_key_specified = true;
                            create_rsa = false;
                        }
                        index++;
                        break;
                    case "rsa":
                        if (has_key_specified)
                        {
                            Console.WriteLine("多次指定密钥类型，操作无效");
                            return;
                        }
                        else
                        {
                            has_key_specified = true;
                            create_rsa = true;
                        }
                        index++;
                        break;
                    default:
                        Console.WriteLine("参数无效：{0}", cmd[index]);
                        return;
                }
            }

            if (_key_manager.HasRsaKey && create_rsa)
                if (enable_force)
                    _key_manager.CreateKey(true);
                else
                {
                    Console.WriteLine("RSA密钥已存在，如需覆盖请使用-F参数，或者调用--delete-key删除");
                    return;
                }
            else if (_key_manager.HasAesKey && !create_rsa)
                if (enable_force)
                    _key_manager.CreateKey(false);
                else
                {
                    Console.WriteLine("AES密钥已存在，如需覆盖请使用-F参数，或者调用--delete-key删除");
                    return;
                }
            else
                _key_manager.CreateKey(create_rsa);

            if (create_rsa)
            {
                Console.WriteLine("已生成RSA密钥，密钥信息：");
                var rsa = new System.Security.Cryptography.RSACryptoServiceProvider();
                rsa.ImportCspBlob(_key_manager.RSAPrivateKey);
                var bit_length = rsa.KeySize;
                Console.WriteLine("\t{0}位RSA密钥", bit_length);
                Console.WriteLine("\t密钥特征码：{0}", util.Hex(MD5.ComputeHash(_key_manager.RSAPrivateKey, 0, _key_manager.RSAPrivateKey.Length)));
            }
            else
            {
                Console.WriteLine("已生成AES密钥，密钥信息：");
                Console.WriteLine("\tAES密钥：{0}", util.Hex(_key_manager.AESKey));
                Console.WriteLine("\tAES初始向量：{0}", util.Hex(_key_manager.AESIV));
            }
        }
        private static void _exec_delete_key(string[] cmd)
        {
            if (cmd.Length < 2)
            {
                Console.WriteLine("参数不足");
                return;
            }
            var key_type = cmd[1];
            if (cmd.Length > 3)
            {
                Console.WriteLine("参数过多");
                return;
            }
            if (key_type != "aes" && key_type != "rsa")
            {
                Console.WriteLine("无效的密钥类型：{0}", key_type);
            }
            bool delete_rsa = key_type == "rsa";

            if (delete_rsa)
            {
                if (_key_manager.HasRsaKey)
                {
                    _key_manager.DeleteKey(true);
                    if (File.Exists("data/rsa_key.pem"))
                        File.Delete("data/rsa_key.pem");
                    Console.WriteLine("删除RSA密钥成功");
                }
                else
                    Console.WriteLine("无RSA密钥，忽略删除操作");
            }
            else
            {
                if (_key_manager.HasAesKey)
                {
                    _key_manager.DeleteKey(false);
                    if (File.Exists("data/aes_key.dat"))
                        File.Delete("data/aes_key.dat");
                    Console.WriteLine("删除AES密钥成功");
                }
                else
                    Console.WriteLine("无AES密钥，忽略删除操作");
            }
        }
        private static void _exec_command(string[] cmd)
        {
            switch (cmd[0])
            {
                case "-L":
                case "--list":
                    _exec_list(cmd);
                    break;
                case "-D":
                case "--download":
                    _exec_download(cmd);
                    break;
                case "-U":
                case "--upload":
                    _exec_upload(cmd);
                    break;
                case "--show-key":
                    _exec_show_key(cmd);
                    break;
                case "--load-key":
                    _exec_load_key(cmd);
                    break;
                case "--save-key":
                    _exec_save_key(cmd);
                    break;
                case "--create-key":
                    _exec_create_key(cmd);
                    break;
                case "--delete-key":
                    _exec_delete_key(cmd);
                    break;
                case "-H":
                case "--help":
                    _print_help();
                    break;
                default:
                    Console.WriteLine("无效的指令：" + cmd[0]);
                    Environment.Exit(0);
                    break;
            }
        }
    }
}
