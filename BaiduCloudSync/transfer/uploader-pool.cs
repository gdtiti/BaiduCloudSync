﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BaiduCloudSync
{
    public class UploaderPool
    {

        //默认并行下载的任务数
        private const int _DEFAULT_POOL_SIZE = 5;
        //外部的同步锁
        private object _external_lock;
        //下载队列
        private Dictionary<int, Uploader> _queue_data;
        //并行任务数
        private int _pool_size;

        //API的封装类
        private RemoteFileCacher _remote_cacher;
        private LocalFileCacher _local_cacher;
        private int _account_id;
        //是否覆盖已有文件
        private bool _overwrite;
        //对该任务池的总下载速度限制
        private int _speed_limit;
        //每个任务的最大下载线程数
        private int _max_thread;
        //是否在下载完成后自动开始新任务的标识，由函数控制
        private bool _auto_start;
        //分配的任务id
        private int _allocated_index;

        private GlobalUtil.KeyManager _key_manager;
        private bool _enable_encrypt;
        public UploaderPool(RemoteFileCacher remote_cacher, LocalFileCacher local_cacher, int account_id, bool overwrite_file = false, GlobalUtil.KeyManager key_manager = null, bool encrypt_file = false)
        {
            if (remote_cacher == null) throw new ArgumentNullException("remote_cacher");
            if (local_cacher == null) throw new ArgumentNullException("local_cacher");
            _remote_cacher = remote_cacher;
            _local_cacher = local_cacher;
            _queue_data = new Dictionary<int, Uploader>();
            _external_lock = new object();
            _pool_size = _DEFAULT_POOL_SIZE;
            _max_thread = Uploader.DEFAULT_MAX_THREAD;
            _auto_start = false;
            _allocated_index = 0;
            _enable_encrypt = encrypt_file;
            _key_manager = key_manager;

            _account_id = account_id;
            _overwrite = overwrite_file;
        }
        ~UploaderPool()
        {
            Dispose();
        }

        #region public properties
        /// <summary>
        /// 并行任务数
        /// </summary>
        public int PoolSize { get { return _pool_size; } set { if (value <= 0) throw new ArgumentOutOfRangeException("value"); lock (_external_lock) _pool_size = value; } }
        /// <summary>
        /// 总上传速度，单位：B/s
        /// </summary>
        public int SpeedLimit { get { return _speed_limit; } set { lock (_external_lock) { _speed_limit = value; _set_speed(); } } }
        /// <summary>
        /// 每个任务的最大线程数
        /// </summary>
        public int MaxThread { get { return _max_thread; } set { lock (_external_lock) _max_thread = value; } }
        //routed events
        public event EventHandler TaskStarted, TaskPaused, TaskCancelled, TaskError, TaskFinished;
        /// <summary>
        /// 上传任务数量
        /// </summary>
        public int Count { get { return _queue_data.Count; } }
        #endregion

        private void _set_speed()
        {
            foreach (var item in _queue_data.Keys)
            {
                _queue_data[item].SpeedLimit = _speed_limit / Math.Min(_queue_data.Count, _pool_size);
            }
        }

        //event callback
        #region event callback
        private void _on_task_started(object sender, EventArgs e)
        {
            try { TaskStarted?.Invoke(sender, e); }
            catch { }
        }
        private void _on_task_paused(object sender, EventArgs e)
        {
            try { TaskPaused?.Invoke(sender, e); }
            catch { }
        }
        private void _on_task_cancelled(object sender, EventArgs e)
        {
            try { TaskCancelled?.Invoke(sender, e); }
            catch { }
        }
        private void _on_task_error(object sender, EventArgs e)
        {
            try { TaskError?.Invoke(sender, e); }
            catch { }
        }
        private void _on_task_finished(object sender, EventArgs e)
        {
            lock (_external_lock)
            {
                if (_auto_start && _queue_data.Count > _pool_size)
                {
                    _queue_data.ElementAt(_pool_size).Value.Start();
                }
                _queue_data.Remove((int)((Uploader)sender).Tag);
                //System.Threading.ThreadPool.QueueUserWorkItem(delegate
                //{
                //    ((Uploader)sender).Dispose();
                //});
                _set_speed();
            }
            try { TaskFinished?.Invoke(sender, e); }
            catch { }
        }
        #endregion

        /// <summary>
        /// 将下载任务添加到下载队列中，返回该任务的队列ID
        /// </summary>
        /// <param name="remote_path">网盘文件路径</param>
        /// <param name="local_path">本地文件路径（父文件夹要求已创建）</param>
        /// <returns></returns>
        public int QueueTask(string remote_path, string local_path)
        {
            lock (_external_lock)
            {
                var uploader = new Uploader(_local_cacher, _remote_cacher, local_path, remote_path, _account_id, _overwrite, _max_thread, _speed_limit / _pool_size, _key_manager, _enable_encrypt);
                var index = _allocated_index++;
                uploader.Tag = index;
                uploader.TaskStarted += _on_task_started;
                uploader.TaskPaused += _on_task_paused;
                uploader.TaskFinished += _on_task_finished;
                uploader.TaskError += _on_task_error;
                uploader.TaskCancelled += _on_task_cancelled;
                _queue_data.Add(index, uploader);
                if (_auto_start && _queue_data.Count <= _pool_size)
                    uploader.Start();
                return index;
            }
        }

        public void Dispose()
        {
            lock (_external_lock)
            {
                foreach (var item in _queue_data)
                {
                    item.Value.Cancel();
                    item.Value.Dispose();
                }
                _queue_data = null;
            }
        }
        /// <summary>
        /// 开始所有任务
        /// </summary>
        public void Start()
        {
            lock (_external_lock)
            {
                _auto_start = true;
                for (int i = 0; i < _pool_size && i < _queue_data.Count; i++)
                {
                    _queue_data[i].Start();
                }
            }
        }
        /// <summary>
        /// 开始指定任务
        /// </summary>
        /// <param name="id">分配的下载id</param>
        public void Start(int id)
        {
            lock (_external_lock)
            {
                if (!_queue_data.ContainsKey(id)) return;
                _queue_data[id].Start();
            }
        }
        /// <summary>
        /// 暂停所有任务
        /// </summary>
        public void Pause()
        {
            lock (_external_lock)
            {
                _auto_start = false;
                for (int i = 0; i < _queue_data.Count; i++)
                {
                    _queue_data[i].Pause();
                }
            }
        }
        /// <summary>
        /// 暂停指定任务
        /// </summary>
        /// <param name="id">分配的下载id</param>
        public void Pause(int id)
        {
            lock (_external_lock)
            {
                if (!_queue_data.ContainsKey(id)) return;
                _queue_data[id].Pause();
            }
        }
        /// <summary>
        /// 取消所有任务
        /// </summary>
        public void Cancel()
        {
            lock (_external_lock)
            {
                _auto_start = false;
                for (int i = 0; i < _queue_data.Count; i++)
                {
                    _queue_data[i].Cancel();
                }
                _queue_data.Clear();
            }
        }
        /// <summary>
        /// 取消指定任务
        /// </summary>
        /// <param name="id">分配的下载id</param>
        public void Cancel(int id)
        {
            lock (_external_lock)
            {
                if (!_queue_data.ContainsKey(id)) return;
                _queue_data[id].Cancel();
                _queue_data.Remove(id);
            }
        }
    }
}
