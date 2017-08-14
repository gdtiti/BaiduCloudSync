﻿// uploader.cs
//
// 用于上传文件的类
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using static BaiduCloudSync.BaiduPCS;

namespace BaiduCloudSync
{
    public class Uploader
    {
        //是否使用秒传api传输
        private const bool _ENABLE_RAPID_UPLOAD = true;

        private string _path;
        private string _local_path;

        private Stream _open_stream;

        private Thread _background_thread;
        private Thread _speed_timer; //controlled by background thread
        private object _thd_lock = new object();
        // 000x(finished) x(md5 calculating) x(cancelled) x(paused) x(inited)
        private byte _state;

        private object _external_lock = new object();
        //秒传相关的变量和结果
        private ulong _content_length;
        private string _content_crc32;
        private string _content_md5;
        private string _slice_md5;
        private bool _rapid_upload_requested;
        //用于申请上传的变量
        private string _uploadid;
        private int _slice_count;
        private List<string> _slice_upload_data;
        //用于计算速度的变量
        private ulong _last_upload_size;
        private ulong _upload_size;
        private ulong _speed;

        private BaiduPCS _api;
        private ondup _ondup;

        private Form _parent_form;
        public Uploader(Form parent, BaiduPCS api, string path, TrackedData local_data, BaiduPCS.ondup ondup = BaiduPCS.ondup.overwrite)
        {
            _path = path;
            _local_path = local_data.Path;
            _parent_form = parent;

            _api = api;
            _ondup = ondup;
            _state = 1;
            _content_length = local_data.ContentSize;
            _content_crc32 = local_data.CRC32;
            _content_md5 = local_data.MD5;

            if (string.IsNullOrEmpty(_content_md5))
                _content_length = (ulong)(new FileInfo(_local_path).Length);
            _rapid_upload_requested = false;
            _slice_count = (int)Math.Ceiling(_content_length / 4194304.0);
            _slice_upload_data = new List<string>();
        }
        private void onStatusUpdated(string path, string local_path, long current, long length)
        {
            _upload_size = (ulong)(current + _slice_upload_data.Count * 4194304);
            //_content_length = (ulong)length;
        }
        private void _speed_timer_callback()
        {
            try
            {
                do
                {
                    _speed = _upload_size - _last_upload_size;
                    if (_speed < 0) _speed = 0;
                    _last_upload_size = _upload_size;
                    Thread.Sleep(1000);
                } while (true);
            }
            catch
            {

            }
            finally
            {
                _speed_timer = null;
            }
        }
        private void _background_thread_callback()
        {
            try
            {
                _speed_timer = new Thread(_speed_timer_callback);
                _speed_timer.IsBackground = true;
                _speed_timer.Name = "速度计算线程";
                _speed_timer.Start();

                if (_content_length == 0) return;
                //calculating local file
                if (!_rapid_upload_requested && _ENABLE_RAPID_UPLOAD)
                {
                    _state = 8;
                    if (string.IsNullOrEmpty(_content_md5))
                    {
                        var rapid_upload_data = _api.GetRapidUploadArguments(_local_path, onStatusUpdated);
                        _content_length = rapid_upload_data.content_length;
                        _content_crc32 = rapid_upload_data.content_crc32;
                        _content_md5 = rapid_upload_data.content_md5;
                        _slice_md5 = rapid_upload_data.slice_md5;
                    }
                    else if (string.IsNullOrEmpty(_slice_md5) && _content_length >= 262144)
                    {
                        var stream = new FileStream(_local_path, FileMode.Open, FileAccess.Read);
                        var md5 = new System.Security.Cryptography.MD5CryptoServiceProvider();
                        int buffer_size = 8192;
                        var buffer = new byte[buffer_size];
                        int rb = 0, tb = 0;
                        do
                        {
                            rb = stream.Read(buffer, 0, buffer_size);
                            if (rb + tb > 262144) rb = 262144 - tb;
                            md5.TransformBlock(buffer, 0, rb, buffer, 0);
                            tb += rb;
                        } while (rb != 0 && tb < 262144);
                        stream.Close();
                        md5.TransformFinalBlock(buffer, 0, 0);
                        var slice_md5 = md5.Hash;
                        _slice_md5 = util.Hex(slice_md5);
                    }
                    ObjectMetadata data = new ObjectMetadata();
                    //posting rapid upload info
                    if (!string.IsNullOrEmpty(_slice_md5))
                    {
                        try
                        {
                            data = _api.RapidUploadRaw(_path, _content_length, _content_md5, _content_crc32, _slice_md5, _ondup);
                            _rapid_upload_requested = true;
                        }
                        catch (ErrnoException ex)
                        {
                            _parent_form.Invoke(new ThreadStart(delegate
                            {
                                MessageBox.Show(_parent_form, "秒传错误: 错误代码: " + ex.Errno, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }));
                        }
                    }
                    if (data.FS_ID != 0)
                    {
                        //rapid upload succeeded, thread exited
                        _background_thread = null;
                        _state = 16;
                        TaskFinished?.Invoke(this, new UploadResultEventArgs(_path, _local_path, true));
                        return;
                    }
                }

                //upload begins
                _state = 0;
                while (string.IsNullOrEmpty(_uploadid))
                {
                    try
                    {
                        _uploadid = _api.PreCreateFile(_path, _slice_count).UploadId;
                    }
                    catch (ErrnoException ex)
                    {
                        Tracer.GlobalTracer.TraceError("Pre-creating upload file failed, response returned errno = " + ex.Errno);
                        _state = 4;
                        TaskCancelled?.Invoke(this, new EventArgs());
                        return;
                    }
                }

                _open_stream = new FileStream(_local_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                while (_slice_upload_data.Count < _slice_count)
                {
                    if (_open_stream.Position != 4194304 * _slice_upload_data.Count)
                        _open_stream.Seek(4194304 * _slice_upload_data.Count, SeekOrigin.Begin);

                    string data = null;
                    try { data = _api.UploadSliceRaw(_open_stream, _path, _uploadid, _slice_upload_data.Count, onStatusUpdated); }
                    catch (ErrnoException ex)
                    {
                        Tracer.GlobalTracer.TraceError("Upload failed, response returned errno = " + ex.Errno);
                        _state = 4;
                        TaskCancelled?.Invoke(this, new EventArgs());
                        return;
                    }

                    if (!string.IsNullOrEmpty(data))
                        _slice_upload_data.Add(data);
                }

                ObjectMetadata dat = new ObjectMetadata();
                while (dat.FS_ID == 0)
                {
                    try
                    {
                        dat = _api.CreateSuperFile(_path, _uploadid, _slice_upload_data, _content_length);
                    }
                    catch (ErrnoException ex)
                    {
                        Tracer.GlobalTracer.TraceError("Create super file failed, response returned errno = " + ex.Errno);
                        _state = 4;
                        TaskCancelled?.Invoke(this, new EventArgs());
                        return;
                    }
                }

                if (dat.FS_ID == 0)
                {
                    //upload failed
                    Tracer.GlobalTracer.TraceWarning("Upload failed, response returned FS_ID = 0 (possibly a bug)");
                    TaskFinished?.Invoke(this, new UploadResultEventArgs(_path, _local_path, false));
                }
                else
                {
                    //checking info
                    if (!string.IsNullOrEmpty(_content_md5) && dat.MD5 != _content_md5)
                    {
                        Tracer.GlobalTracer.TraceWarning("[MD5 CHECK]: Upload file MD5 mismatch! response returned " + dat.MD5 + " (expected: " + _content_md5 + ")");
                        TaskFinished?.Invoke(this, new UploadResultEventArgs(_path, _local_path, false));
                    }
                    else if (dat.Size != _content_length)
                    {
                        Tracer.GlobalTracer.TraceWarning("[LENGTH CHECK]: Upload file Length mismatch! response returned " + dat.Size + " (expected: " + _content_length + ")");
                        TaskFinished?.Invoke(this, new UploadResultEventArgs(_path, _local_path, false));
                    }
                    else
                    {
                        TaskFinished?.Invoke(this, new UploadResultEventArgs(_path, _local_path, true));
                    }

                }
                _state = 0;
                _background_thread = null;
            }
            catch (Exception)
            {
            }
            finally
            {
                if (_speed_timer != null)
                    _speed_timer.Abort();
                if (_open_stream != null)
                    _open_stream.Close();
            }

        }
        public void Start()
        {
            if (_state != 2 && _state != 1) return;
            lock (_external_lock)
            {
                _state = 0;
                _background_thread = new Thread(_background_thread_callback);
                _background_thread.IsBackground = true;
                _background_thread.Name = "上传线程";
                _background_thread.Start();
                TaskStarted?.Invoke(this, new EventArgs());
            }
        }
        public void Pause()
        {
            if (_state != 0 && _state != 8) return;
            lock (_external_lock)
            {
                _state = 2;
                if (_background_thread != null)
                    _background_thread.Abort();
                _background_thread = null;
                _upload_size = (ulong)(4194304 * _slice_upload_data.Count);
                _last_upload_size = _upload_size;
                TaskPaused?.Invoke(this, new EventArgs());
            }
        }

        public void Cancel()
        {
            if (_state == 4 || _state == 16) return;
            lock (_external_lock)
            {
                if (_background_thread != null)
                {
                    try
                    {
                        _background_thread.Abort();
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        _background_thread = null;
                        _state = 4;
                    }
                }
                TaskCancelled?.Invoke(this, new EventArgs());
            }
        }

        public bool IsStarted { get { return _state == 0 || _state == 8; } }
        public bool IsPaused { get { return _state == 2; } }
        public bool IsCancelled { get { return _state == 4; } }
        public bool IsCalculatingMD5 { get { return _state == 8; } }
        public bool IsFinished { get { return _state == 16; } }
        public bool IsInitialized { get { return _state == 1; } }
        public ulong Uploaded_Size { get { return _upload_size; } }
        public ulong Content_Length { get { return _content_length; } }
        public string FileMD5 { get { return _content_md5; } }
        public string FileName { get { return _path.Split('/').Last(); } }
        public double Finish_Rate { get { return (_content_length == 0) ? 0 : (1.0 * _upload_size / _content_length); } }

        public ulong Speed { get { return _speed; } }
        public event EventHandler TaskStarted, TaskPaused, TaskCancelled;

        public event EventHandler<UploadResultEventArgs> TaskFinished;
    }
    public class UploadResultEventArgs : EventArgs
    {
        public readonly string RemotePath;
        public readonly string LocalPath;
        public readonly bool Succeeded;
        public UploadResultEventArgs(string _remote_path, string _local_path, bool _succeeded)
        {
            RemotePath = _remote_path;
            LocalPath = _local_path;
            Succeeded = _succeeded;
        }
    }

}
