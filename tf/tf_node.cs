﻿#region USINGZ

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Messages;
using Messages.std_msgs;
using Messages.tf;
using Ros_CSharp;
using gm = Messages.geometry_msgs;
using Int64 = System.Int64;
using String = Messages.std_msgs.String;

#endregion

namespace tf
{
    public class Stamped<T>
    {
        public Time stamp;
        public string frame_id;
        public T data;

        public Stamped()
        {
        }

        public Stamped(Time t, string f, T d)
        {
            stamp = t;
            frame_id = f;
            data = d;
        }
    }

    // Listenes to the /tf topic, need subscriber
    // for each Transform in /tf, create a new frame. Frame has a (frame)child and (frame)id
    // provide translation from 2 frames, user requests from /map to /base_link for example, must identify route
    // base_link.child = odom, odom.child = map
    // map-> odom + odom->base_link
    internal class tf_node
    {
        private static tf_node _instance;
        private static object singleton_mutex = new object();

        private Queue<tfMessage> additions;
        private object addlock = new object();
        private object frameslock = new object();
        private NodeHandle tfhandle;
        private Thread updateThread;

        internal delegate void TFUpdate(gm.TransformStamped msg);

        internal event TFUpdate TFsUpdated;

        private tf_node()
        {
            if (additions == null)
                additions = new Queue<tfMessage>();
            if (tfhandle == null)
            {
                tfhandle = new NodeHandle();
            }
            if (updateThread == null)
            {
                updateThread = new Thread(() =>
                {
                    while (ROS.ok)
                    {
                        Queue<tfMessage> local;
                        lock (addlock)
                        {
                            local = new Queue<tfMessage>(additions);
                            additions.Clear();
                        }
                        if (local.Count > 0)
                        {
                            while (local.Count > 0)
                            {
                                tfMessage msg = local.Dequeue();
                                foreach (gm.TransformStamped t in msg.transforms)
                                {
                                    if (TFsUpdated != null)
                                    {
                                        TFsUpdated(t);
                                    }
                                }
                            }
                        }
                        Thread.Sleep(1);
                    }
                });
                updateThread.Start();
            }
            tfhandle.subscribe<tfMessage>("/tf", 0, tfCallback);
        }

        public static tf_node instance
        {
            [DebuggerStepThrough]
            get
            {
                if (_instance == null)
                {
                    lock (singleton_mutex)
                    {
                        if (_instance == null)
                            _instance = new tf_node();
                    }
                }
                return _instance;
            }
        }

        private void tfCallback(tfMessage msg)
        {
            lock (addlock)
                additions.Enqueue(msg);
        }
    }

    public enum TF_STATUS
    {
        NO_ERROR,
        LOOKUP_ERROR,
        CONNECTIVITY_ERROR,
        EXTRAPOLATION_ERROR
    }

    public class Transformer
    {
        private const string tf_prefix = "/";
        private const uint MAX_GRAPH_DEPTH = 100;
        private const double DEFAULT_CACHE_TIME = 1000000000;
        private const ulong DEFAULT_MAX_EXTRAPOLATION_DISTANCE = 0;
        private ulong cache_time;

        private Dictionary<string, uint> frameIDs = new Dictionary<string, uint> { { "NO_PARENT", 0 } };
        private Dictionary<uint, string> frameids_reverse = new Dictionary<uint, string> { { 0, "NO_PARENT" } };
        private object framemutex = new object();
        private SortedList<uint, TimeCache> frames = new SortedList<uint, TimeCache>();

        private bool interpolating;
        private tf_node tfnode = null;

        public Transformer(bool interpolating = true, ulong ct = (ulong) DEFAULT_CACHE_TIME)
        {
            if (ROS.isStarted() && !ROS.shutting_down)
            {
                tf_node.instance.TFsUpdated += Update;
            }
            this.interpolating = interpolating;
            cache_time = ct;
        }

        private void Update(gm.TransformStamped msg)
        {
            if (!setTransform(new emTransform(msg)))
                ROS.Warn("Failed to setTransform in transformer update function");
        }

        public static string resolve(string prefix, string frame_name)
        {
            if (frame_name.Length > 0)
            {
                if (frame_name[0] == '/')
                    return frame_name;
            }
            if (prefix.Length > 0)
            {
                if (prefix[0] == '/' && prefix.Length == 1)
                    return "/" + frame_name;
                else if (prefix[0] == '/')
                    return prefix + "/" + frame_name;
                return "/" + prefix + "/" + frame_name;
            }
            return "/" + frame_name;
        }

        public void clear()
        {
            lock (framemutex)
            {
                foreach (TimeCache tc in frames.Values)
                    tc.clearList();
            }
        }

        public void lookupTransform(String t, String s, Time time, out emTransform transform)
        {
            try
            {
                lookupTransform(t.data, s.data, time, out transform);
            }
            catch (Exception e)
            {
                transform = null;
                ROS.Error(e);
                throw e;
            }
        }

        public uint getFrameID(string frame)
        {
            if (frameIDs.ContainsKey(frame))
            {
                return frameIDs[frame];
            }
            return 0;
        }

        public bool lookupTransform(string target_frame, string source_frame, Time time, out emTransform transform)
        {
            string error_string = null;
            bool result = lookupTransform(target_frame, source_frame, time, out transform, ref error_string);
            if (!result && error_string != null)
                ROS.Error(error_string);
            return result;
        }

        public bool lookupTransform(string target_frame, string source_frame, Time time, out emTransform transform, ref string error_string)
        {
            transform = new emTransform();

            string mapped_tgt = resolve(tf_prefix, target_frame);
            string mapped_src = resolve(tf_prefix, source_frame);

            if (mapped_tgt == mapped_src)
            {
                transform.origin = new emVector3();
                transform.basis = new emQuaternion();
                transform.child_frame_id = mapped_src;
                transform.frame_id = mapped_tgt;
                transform.stamp = ROS.GetTime(DateTime.Now);
                return true;
            }

            TF_STATUS retval;
            lock (framemutex)
            {
                uint target_id = getFrameID(mapped_tgt);
                uint source_id = getFrameID(mapped_src);

                TransformAccum accum = new TransformAccum();

                retval = walkToTopParent(accum, TimeCache.toLong(time.data), target_id, source_id, ref error_string);
                if (error_string != null && retval != TF_STATUS.NO_ERROR)
                {
                    switch (retval)
                    {
                        case TF_STATUS.CONNECTIVITY_ERROR:
                            ROS.Error("NO CONNECTIONSZSZ: " + error_string);
                            break;
                        case TF_STATUS.EXTRAPOLATION_ERROR:
                            ROS.Error("EXTRAPOLATION: " + error_string);
                            break;
                        case TF_STATUS.LOOKUP_ERROR:
                            ROS.Error("LOOKUP: " + error_string);
                            break;
                    }
                }
                transform.origin = accum.result_vec;
                transform.basis = accum.result_quat;
                transform.child_frame_id = mapped_src;
                transform.frame_id = mapped_tgt;
                transform.stamp = new Time { data = new TimeData { sec = (uint)(accum.time >> 32), nsec = (uint)(accum.time & 0xFFFFFFFF) } };
            }
            return retval == TF_STATUS.NO_ERROR;
        }

        public void transformQuaternion(string target_frame, Stamped<emQuaternion> stamped_in, ref Stamped<emQuaternion> stamped_out)
        {
            emTransform trans = new emTransform();
            lookupTransform(target_frame, stamped_in.frame_id, stamped_in.stamp, out trans);
            stamped_out.data = trans * stamped_in.data;
            stamped_out.stamp = trans.stamp;
            stamped_out.frame_id = target_frame;
        }

        public void transformQuaternion(string target_frame, Stamped<gm.Quaternion> stamped_in, ref Stamped<gm.Quaternion> stamped_out)
        {
            Stamped<emQuaternion> quatin = new Stamped<emQuaternion>(stamped_in.stamp, stamped_in.frame_id, new emQuaternion(stamped_in.data));
            Stamped<emQuaternion> quatout = new Stamped<emQuaternion>(stamped_out.stamp, stamped_out.frame_id, new emQuaternion(stamped_out.data));
            transformQuaternion(target_frame, quatin, ref quatout);
            stamped_out.stamp = quatout.stamp;
            stamped_out.data = quatout.data.ToMsg();
            stamped_out.frame_id = quatout.frame_id;
        }

        public void transformVector(string target_frame, Stamped<emVector3> stamped_in, ref Stamped<emVector3> stamped_out)
        {
            emTransform trans = new emTransform();
            lookupTransform(target_frame, stamped_in.frame_id, stamped_in.stamp, out trans);
            emVector3 end = stamped_in.data;
            emVector3 origin = new emVector3(0, 0, 0);
            emVector3 output = (trans * end) - (trans * origin);
            stamped_out.data = output;
            stamped_out.stamp = trans.stamp;
            stamped_out.frame_id = target_frame;
        }

        public void transformVector(string target_frame, Stamped<gm.Vector3> stamped_in, ref Stamped<gm.Vector3> stamped_out)
        {
            Stamped<emVector3> vecin = new Stamped<emVector3>(stamped_in.stamp, stamped_in.frame_id, new emVector3(stamped_in.data));
            Stamped<emVector3> vecout = new Stamped<emVector3>(stamped_out.stamp, stamped_out.frame_id, new emVector3(stamped_out.data));
            transformVector(target_frame, vecin, ref vecout);
            stamped_out.stamp = vecout.stamp;
            stamped_out.data = vecout.data.ToMsg();
            stamped_out.frame_id = vecout.frame_id;
        }

        public void transformPoint(string target_frame, Stamped<gm.Point> stamped_in, ref Stamped<gm.Point> stamped_out)
        {

        }

        public TF_STATUS walkToTopParent<F>(F f, ulong time, uint target_id, uint source_id, ref string error_str) where F : ATransformAccum
        {
            if (target_id == source_id)
            {
                f.finalize(WalkEnding.Identity, time);
                return TF_STATUS.NO_ERROR;
            }
            if (time == 0)
            {
                TF_STATUS retval = getLatestCommonTime(target_id, source_id, ref time, ref error_str);
                if (retval != TF_STATUS.NO_ERROR)
                    return retval;
            }
            uint frame = source_id;
            uint top_parent = frame;
            uint depth = 0;
            while (frame != 0)
            {
                if (!frames.ContainsKey(frame))
                {
                    top_parent = frame;
                    break;
                }
                TimeCache cache = frames[frame];
                uint parent = f.gather(cache, time, ref error_str);
                if (parent == 0)
                {
                    top_parent = frame;
                    break;
                }

                if (frame == target_id)
                {
                    f.finalize(WalkEnding.TargetParentOfSource, time);
                    return TF_STATUS.NO_ERROR;
                }

                f.accum(true);

                top_parent = frame;
                frame = parent;
                ++depth;
                if (depth > MAX_GRAPH_DEPTH)
                {
                    if (error_str != null)
                    {
                        error_str = "The tf tree is invalid because it contains a loop.";
                    }
                    return TF_STATUS.LOOKUP_ERROR;
                }
            }

            frame = target_id;
            depth = 0;
            while (frame != top_parent)
            {
                if (!frames.ContainsKey(frame))
                    break;
                TimeCache cache = frames[frame];

                uint parent = f.gather(cache, time, ref error_str);

                if (parent == 0)
                {
                    if (error_str != null)
                    {
                        error_str += ", when looking up transform from frame [" + frameids_reverse[source_id] + "] to [" + frameids_reverse[target_id] + "]";
                    }
                    return TF_STATUS.EXTRAPOLATION_ERROR;
                }

                if (frame == source_id)
                {
                    f.finalize(WalkEnding.SourceParentOfTarget, time);
                    return TF_STATUS.NO_ERROR;
                }

                f.accum(false);

                frame = parent;
                ++depth;
                if (depth > MAX_GRAPH_DEPTH)
                {
                    if (error_str != null)
                    {
                        error_str = "The tf tree is invalid because it contains a loop.";
                    }
                    return TF_STATUS.LOOKUP_ERROR;
                }
            }


            if (frame != top_parent)
            {
                if (error_str != null)
                    error_str = "" + frameids_reverse[source_id] + " DOES NOT CONNECT TO " + frameids_reverse[target_id];
                return TF_STATUS.CONNECTIVITY_ERROR;
            }

            f.finalize(WalkEnding.FullPath, time);

            return TF_STATUS.NO_ERROR;
        }

        private TF_STATUS getLatestCommonTime(uint target_id, uint source_id, ref ulong time, ref string error_str)
        {
            if (target_id == source_id)
            {
                time = TimeCache.toLong(ROS.GetTime(DateTime.Now).data);
                return TF_STATUS.NO_ERROR;
            }

            List<TimeAndFrameID> lct = new List<TimeAndFrameID>();

            uint frame = source_id;
            TimeAndFrameID temp;
            uint depth = 0;
            ulong common_time = ulong.MaxValue;
            while (frame != 0)
            {
                TimeCache cache;
                if (!frames.ContainsKey(frame)) break;
                cache = frames[frame];
                TimeAndFrameID latest = cache.getLatestTimeAndParent();
                if (latest.frame_id == 0)
                    break;
                if (latest.time != 0)
                    common_time = Math.Min(latest.time, common_time);
                lct.Add(latest);
                frame = latest.frame_id;
                if (frame == target_id)
                {
                    time = common_time;
                    if (time == ulong.MaxValue)
                        time = 0;
                    return TF_STATUS.NO_ERROR;
                }
                ++depth;
                if (depth > MAX_GRAPH_DEPTH)
                {
                    if (error_str != null)
                    {
                        error_str = "The tf tree is invalid because it contains a loop.";
                    }
                    return TF_STATUS.LOOKUP_ERROR;
                }
            }

            frame = target_id;
            depth = 0;
            common_time = ulong.MaxValue;
            uint common_parent = 0;
            while (true)
            {
                TimeCache cache;
                if (!frames.ContainsKey(frame))
                    break;
                cache = frames[frame];
                TimeAndFrameID latest = cache.getLatestTimeAndParent();
                if (latest.frame_id == 0)
                    break;
                if (latest.time != 0)
                    common_time = Math.Min(latest.time, common_time);

                foreach (TimeAndFrameID tf in lct)
                    if (tf.frame_id == latest.frame_id)
                    {
                        common_parent = tf.frame_id;
                        break;
                    }
                frame = latest.frame_id;

                if (frame == source_id)
                {
                    time = common_time;
                    if (time == uint.MaxValue)
                    {
                        time = 0;
                    }
                    return TF_STATUS.NO_ERROR;
                }
                ++depth;
                if (depth > MAX_GRAPH_DEPTH)
                {
                    if (error_str != null)
                    {
                        error_str = "The tf tree is invalid because it contains a loop.";
                    }
                    return TF_STATUS.LOOKUP_ERROR;
                }
            }
            if (common_parent == 0)
            {
                error_str = "" + frameids_reverse[source_id] + " DOES NOT CONNECT TO " + frameids_reverse[target_id];
                return TF_STATUS.CONNECTIVITY_ERROR;
            }
            for (int i = 0; i < lct.Count; i++)
            {
                if (lct[i].time != 0)
                    common_time = Math.Min(common_time, lct[i].time);
                if (lct[i].frame_id == common_parent)
                    break;
            }
            if (common_time == uint.MaxValue)
                common_time = 0;
            time = common_time;
            return TF_STATUS.NO_ERROR;
        }

        public uint lookupOrInsertFrameNumber(string frame)
        {
            if (!frameIDs.ContainsKey(frame))
            {
                frameIDs.Add(frame, (uint)(frameIDs.Count + 1));
                frameids_reverse.Add((uint)frameids_reverse.Count + 1, frame);
            }
            return frameIDs[frame];
        }

        public bool setTransform(emTransform transform)
        {
            emTransform mapped_transform = new emTransform(transform.basis, transform.origin, transform.stamp, transform.frame_id, transform.child_frame_id);
            mapped_transform.child_frame_id = resolve(tf_prefix, transform.child_frame_id);
            mapped_transform.frame_id = resolve(tf_prefix, transform.frame_id);

            if (mapped_transform.child_frame_id == mapped_transform.frame_id)
                return false;
            if (mapped_transform.child_frame_id == "/")
                return false;
            if (mapped_transform.frame_id == "/")
                return false;
            lock (framemutex)
            {
                uint frame_number = lookupOrInsertFrameNumber(mapped_transform.child_frame_id);
                TimeCache frame = null;
                if (!frames.ContainsKey(frame_number))
                {
                    frames[frame_number] = new TimeCache(cache_time);
                    frame = frames[frame_number];
                }
                else
                    frame = frames[frame_number];
                if (frame.insertData(new TransformStorage(mapped_transform, lookupOrInsertFrameNumber(mapped_transform.frame_id), frame_number)))
                {
                    // authority? meh
                }
                else
                    return false;
            }
            return true;
        }
    }

    public struct TimeAndFrameID
    {
        public uint frame_id;
        public ulong time;

        public TimeAndFrameID(ulong t, uint f)
        {
            time = t;
            frame_id = f;
        }
    }

    public class TimeCache
    {
        private const int MIN_INTERPOLATION_DISTANCE = 5;
        private const uint MAX_LENGTH_LINKED_LIST = 10000000;
        private const Int64 DEFAULT_MAX_STORAGE_TIME = 1000000000;

        private ulong max_storage_time;
        private DLL<TransformStorage> storage = new DLL<TransformStorage>();

        public TimeCache()
            : this(DEFAULT_MAX_STORAGE_TIME)
        {
        }

        public TimeCache(ulong max_storage_time)
        {
            this.max_storage_time = max_storage_time;
        }

        public static ulong toLong(TimeData td)
        {
            return (((ulong)td.sec) << 32) | td.nsec;
        }

        private byte findClosest(ref TransformStorage one, ref TransformStorage two, ulong target_time, ref string error_str)
        {
            if (storage.Count == 0)
            {
                createEmptyException(ref error_str);
                return 0;
            }

            if (target_time == 0)
            {
                one = storage.Back;
                return 1;
            }

            if (storage.Count == 1)
            {
                TransformStorage ts = storage.Front;
                if (ts.stamp == target_time)
                {
                    one = ts;
                    return 1;
                }
                createExtrapolationException1(target_time, ts.stamp, ref error_str);
                return 0;
            }

            ulong latest_time = storage.Back.stamp;
            ulong earliest_time = storage.Front.stamp;
            if (target_time == latest_time)
            {
                one = storage.Back;
                return 1;
            }
            if (target_time == earliest_time)
            {
                one = storage.Front;
                return 1;
            }
            if (target_time > latest_time)
            {
                createExtrapolationException2(target_time, latest_time, ref error_str);
                return 0;
            }
            if (target_time < earliest_time)
            {
                createExtrapolationException3(target_time, earliest_time, ref error_str);
                return 0;
            }

            ulong i;
            for (i = 0; i < storage.Count; i++)
            {
                if (storage[i].stamp <= target_time) break;
            }
            one = storage[i + 1];
            two = storage[i];
            return 2;
        }

        private void interpolate(TransformStorage one, TransformStorage two, ulong time, ref TransformStorage output)
        {
            if (one.stamp == two.stamp)
            {
                output = two;
                return;
            }

            if (output == null)
                output = new TransformStorage();

            double ratio = (time - one.stamp) / (two.stamp - one.stamp);
            output.translation.setInterpolate3(one.translation, two.translation, ratio);
            output.rotation = slerp(one.rotation, two.rotation, ratio);
            output.stamp = one.stamp;
            output.frame_id = one.frame_id;
            output.child_frame_id = one.child_frame_id;
        }

        private emQuaternion slerp(emQuaternion q1, emQuaternion q2, double rt)
        {
            return q1.slerp(q2, rt);
        }

        private void pruneList()
        {
            ulong latest_time = storage.Back.stamp;
            ulong preprune = storage.Count, postprune = 0;
            while ((postprune = storage.Count) > 0 && storage.Front.stamp + max_storage_time < latest_time)
                storage.popFront();
            //Console.WriteLine("Pruned " + (preprune - postprune) + " transforms. " + postprune + " remain");
        }

        public bool getData(TimeData time_, ref TransformStorage data_out, ref string error_str)
        {
            return getData(toLong(time_), ref data_out, ref error_str);
        }

        public bool getData(ulong time_, ref TransformStorage data_out, ref string error_str)
        {
            TransformStorage temp1 = null, temp2 = null;
            int num_nodes;
            num_nodes = findClosest(ref temp1, ref temp2, time_, ref error_str);
            switch (num_nodes)
            {
                case 0:
                    return false;
                case 1:
                    data_out = temp1;
                    break;
                case 2:
                    if (temp1.frame_id == temp2.frame_id)
                    {
                        interpolate(temp1, temp2, time_, ref data_out);
                    }
                    else
                    {
                        data_out = temp1;
                    }
                    break;
                default:
                    ROS.FREAKOUT();
                    break;
            }
            return true;
        }

        public bool insertData(TransformStorage new_data)
        {
            if (storage.Count > 0 && storage.Front.stamp > new_data.stamp + max_storage_time)
                return false;

            storage.insert(new_data, (a, b) => a.stamp > new_data.stamp);
            pruneList();
            return true;
        }

        public void clearList()
        {
            storage.Clear();
        }

        public uint getParent(ulong time, ref string error_str)
        {
            TransformStorage temp1 = null, temp2 = null;

            int num_nodes;
            num_nodes = findClosest(ref temp1, ref temp2, time, ref error_str);
            if (num_nodes == 0) return 0;
            return temp1.frame_id;
        }

        public uint getParent(TimeData time_, ref string error_str)
        {
            return getParent(toLong(time_), ref error_str);
        }

        public TimeAndFrameID getLatestTimeAndParent()
        {
            if (storage.Count == 0)
            {
                return new TimeAndFrameID(0, 0);
            }
            TransformStorage ts = storage.Back;
            return new TimeAndFrameID(ts.stamp, ts.frame_id);
        }

        public uint getListLength()
        {
            return (uint)storage.Count;
        }

        public ulong getLatestTimeStamp()
        {
            if (storage.Count == 0) return 0;
            return storage.Back.stamp;
        }

        public ulong getOldestTimestamp()
        {
            if (storage.Count == 0) return 0;
            return storage.Front.stamp;
        }

        #region ERROR THROWERS

        private void createEmptyException(ref string error_str)
        {
            if (error_str != null) error_str = "Cache is empty!";
        }

        private void createExtrapolationException1(ulong t0, ulong t1, ref string error_str)
        {
            if (error_str != null) error_str = "Lookup would require extrapolation at time \n" + t0 + ", but only time \n" + t1 + " is in the buffer";
        }

        private void createExtrapolationException2(ulong t0, ulong t1, ref string error_str)
        {
            if (error_str != null) error_str = "Lookup would require extrapolation into the future. Requested time \n" + t0 + " but the latest data is at the time \n" + t1;
        }

        private void createExtrapolationException3(ulong t0, ulong t1, ref string error_str)
        {
            if (error_str != null) error_str = "Lookup would require extrapolation into the past. Requested time \n" + t0 + " but the earliest data is at the time \n" + t1;
        }

        #endregion
    }

    [DebuggerStepThrough]
    public class TransformStorage
    {
        public uint child_frame_id;
        public uint frame_id;
        public emQuaternion rotation;
        public ulong stamp;
        public emVector3 translation;

        public TransformStorage()
        {
            rotation = new emQuaternion();

            translation = new emVector3();
        }

        public TransformStorage(emTransform data, uint frame_id, uint child_frame_id)
        {
            rotation = data.basis;
            translation = data.origin;
            stamp = TimeCache.toLong(data.stamp.data);
            this.frame_id = frame_id;
            this.child_frame_id = child_frame_id;
        }
    }

    public enum WalkEnding
    {
        Identity,
        TargetParentOfSource,
        SourceParentOfTarget,
        FullPath
    }

    public abstract class ATransformAccum
    {
        public TransformStorage st;
        public abstract uint gather(TimeCache cache, ulong time, ref string error_str);
        public abstract void accum(bool source);
        public abstract void finalize(WalkEnding end, ulong time);
    }

    public class CanTransformAccum : ATransformAccum
    {
        public override uint gather(TimeCache cache, ulong time, ref string error_str)
        {
            return cache.getParent(time, ref error_str);
        }

        public override void accum(bool source)
        {
        }

        public override void finalize(WalkEnding end, ulong time)
        {
        }
    }

    public class TransformAccum : ATransformAccum
    {
        public emQuaternion result_quat;
        public emVector3 result_vec;
        public emQuaternion source_to_top_quat = new emQuaternion();
        public emVector3 source_to_top_vec = new emVector3();
        public emQuaternion target_to_top_quat = new emQuaternion();
        public emVector3 target_to_top_vec = new emVector3();
        public ulong time;

        public override uint gather(TimeCache cache, ulong time_, ref string error_str)
        {
            if (!cache.getData(time_, ref st, ref error_str))
                return 0;
            return st.frame_id;
        }

        public override void finalize(WalkEnding end, ulong _time)
        {
            switch (end)
            {
                case WalkEnding.Identity:
                    break;
                case WalkEnding.TargetParentOfSource:
                    result_vec = source_to_top_vec;
                    result_quat = source_to_top_quat;
                    break;
                case WalkEnding.SourceParentOfTarget:
                    {
                        emQuaternion inv_target_quat = target_to_top_quat.inverse();
                        emVector3 inv_target_vec = quatRotate(inv_target_quat, -1 * target_to_top_vec);
                        result_quat = inv_target_quat;
                        result_vec = inv_target_vec;
                    }
                    break;
                case WalkEnding.FullPath:
                    {
                        emQuaternion inv_target_quat = target_to_top_quat.inverse();
                        emVector3 inv_target_vec = quatRotate(inv_target_quat, -1 * target_to_top_vec);
                        result_vec = quatRotate(inv_target_quat, source_to_top_vec) + inv_target_vec;
                        result_quat = inv_target_quat * source_to_top_quat;
                    }
                    break;
            }
            time = _time;
        }

        public override void accum(bool source)
        {
            if (source)
            {
                source_to_top_vec = quatRotate(st.rotation, source_to_top_vec) + st.translation;
                source_to_top_quat = st.rotation * source_to_top_quat;

            }
            else
            {
                target_to_top_vec = quatRotate(st.rotation, target_to_top_vec) + st.translation;
                target_to_top_quat = st.rotation * target_to_top_quat;
            }
        }

        public emVector3 quatRotate(emQuaternion rotation, emVector3 v)
        {
            emQuaternion q = new emQuaternion(rotation * new emQuaternion(1.0, v.x, v.y, v.z));
            q = q * rotation.inverse();
            return new emVector3(q.x, q.y, q.z);
        }
    }
}