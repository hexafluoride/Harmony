using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Harmony
{
    [MessagePackObject]
    public class Lock
    {
        public static Random Random = new Random();

        [Key(0)]
        public byte[] ID { get; set; }

        [Key(1)]
        public byte[] RequesterID { get; set; }

        [Key(2)]
        public DateTime Acquired { get; set; }

        [Key(3)]
        public DateTime Expires { get; set; }

        [IgnoreMember]
        public bool Expired => DateTime.UtcNow > Expires;

        public Lock() { }
        public Lock(byte[] id, int seconds = 30)
        {
            ID = new byte[id.Length];

            lock (Random)
            {
                Random.NextBytes(ID);
            }

            RequesterID = id;
            Acquired = DateTime.UtcNow;
            Expires = Acquired + TimeSpan.FromSeconds(seconds);
        }
    }
}
