﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

using SF.Space;

namespace SF.ServerLibrary
{
    public sealed class Universe
    {
        private static readonly Random Random = new Random();
        private static readonly SynchronizationContext Context = SynchronizationContext.Current;
        private object m_locker = new object();

        public const int SmallDelay = 100;

        private readonly System.Diagnostics.Stopwatch m_stopWatch = new System.Diagnostics.Stopwatch();

        private readonly IDictionary<string, IHelm> m_helms;
        private readonly IDictionary<string, Star> m_stars;
        private readonly IList<IMissile> m_missiles;
        private readonly Thread m_backgroundWorker;

        private string SerializeObject<T>(T instance)
        {
            var serializer = new XmlSerializer(instance.GetType());
            var writer = new StringWriter();
            serializer.Serialize(writer, instance);
            writer.Close();
            return writer.ToString();
        }

        private T DeserializeObject<T>(string source)
        {
            var serializer = new XmlSerializer(typeof(T));
            var reader = new StringReader(source);
            var read = (T)serializer.Deserialize(reader);
            reader.Close();
            return read;
        }

        private string SerializeCollection<T, U>(IEnumerable<T> collection) where U : T
        {
            var list = collection.Cast<U>().ToArray();
            var serializer = new XmlSerializer(list.GetType());
            var writer = new StringWriter();
            serializer.Serialize(writer, list);
            writer.Close();
            return writer.ToString();
        }

        private U[] DeserializeCollection<U>(string source)
        {
            var serializer = new XmlSerializer(typeof(U[]));
            var reader = new StringReader(source);
            var read = (U[])serializer.Deserialize(reader);
            return read;
        }

        public Universe()
        {
            var catalog = File.ReadAllText("catalog.xml");
            var catalogDefinition = this.DeserializeObject<CatalogDefinition>(catalog);
            Catalog.Create(catalogDefinition);
            var ships = File.ReadAllText("helms.xml");
            var helms = this.DeserializeCollection<HelmDefinition>(ships);
            m_helms = helms.Select(Helm.Load).ToDictionary(ship => ship.Ship.Name);
            m_missiles = new List<IMissile>();
            var stars = File.ReadAllText("stars.xml");
            m_stars = this.DeserializeCollection<Star>(stars).ToDictionary(star => star.Name);
            this.m_backgroundWorker = new Thread(this.TimingThreadStart) { IsBackground = true };
        }

        public bool IsRunning
        {
            get { return this.m_stopWatch.IsRunning; }
            set
            {
                if (this.m_stopWatch.IsRunning == value)
                    return;
                if (value)
                {
                    this.m_stopWatch.Start();
                    if (!this.m_backgroundWorker.IsAlive)
                        this.m_backgroundWorker.Start();
                }
                else
                    this.m_stopWatch.Stop();
            }
        }

        public TimeSpan Time
        {
            get { return this.m_stopWatch.Elapsed; }
        }

        public View GetView(IHelm m_helm)
        {
            lock (m_locker)
            {
                return new View
                {
                    Time = Time,
                    Helm = HelmDefinition.Store(m_helm),
                    Ships = GetVisibleShips(m_helm).Select(ShipDefinition.Store).ToArray(),
                    Missiles = GetVisibleMissiles(m_helm).Select(MissileDefinition.Store).ToArray(),
                    Stars = GetStars().ToArray(),
                };
            }
        }

        public CatalogDefinition GetCatalog(string nation)
        {
            lock (m_locker)
            {
                var shipClassesByNation = Catalog.Instance.ShipClasses.Values.Where(c => c.Nation == nation);
                var shipClassesByShips = this.m_helms.Where(i => i.Value.Ship.Nation == nation).Select(i => i.Value.Ship.Class);
                var missileClassesByNation = Catalog.Instance.MissileClasses.Values.Where(c => c.Nation == nation);
                var missileClassesByShips = this.m_helms.Where(i => i.Value.Ship.Nation == nation).Select(i => i.Value.Ship.Missile);
                return new CatalogDefinition
                {
                    MaximumMissileRange = Catalog.Instance.MaximumMissileRange,
                    SkirtAngle = Catalog.Instance.SkirtAngle,
                    ThroatAngle = Catalog.Instance.ThroatAngle,
                    DefaultScale = Catalog.Instance.DefaultScale,
                    ShipClasses = shipClassesByNation.Union(shipClassesByShips).Distinct().ToArray(),
                    MissileClasses = missileClassesByNation.Union(missileClassesByShips).Distinct().ToArray(),
                };
            }
        }

        public IHelm GetHelm(string nation, string name)
        {
            lock (m_locker)
            {
                var helm = GetHelm(name);
                if (helm.Ship.Nation != nation)
                    return null;
                return helm;
            }
        }

        public void Fire(IShip from, bool left, string to, int number)
        {
            lock (m_locker)
            {
                number = Math.Min(number, from.Missiles);
                var target = GetHelm(to);
                if (from.Missile == null || target == null || number <= 0)
                    return;
                var result = new Missile(from, left, target.Ship, number, Time);
                m_missiles.Add(result);
            }
        }

        private IHelm GetHelm(string name)
        {
            IHelm result;
            if (!m_helms.TryGetValue(name, out result))
                return null;
            return result;
        }

        private IEnumerable<IShip> GetVisibleShips(IHelm me)
        {
            return this.m_helms.Where(i => i.Value != me).Select(i => i.Value.Ship); 
        }

        private IEnumerable<IMissile> GetVisibleMissiles(IHelm me)
        {
            return this.m_missiles;
        }

        private IEnumerable<Star> GetStars()
        {
            return this.m_stars.Values;
        }

        public KeyValuePair<string, string[]>[] GetShipNames()
        {
            lock (m_locker)
            {
                var nations = m_helms.Select(i => i.Value.Ship.Nation).Distinct().ToList();
                nations.Sort();
                int n = nations.Count;
                var result = new KeyValuePair<string, string[]>[n];
                for (int i = 0; i < n; i++)
                {
                    var nation = nations[i];
                    var ships = m_helms.Where(h => h.Value.Ship.Nation == nation).Select(h => h.Value.Ship.Name).Distinct().ToList();
                    ships.Sort();
                    result[i] = new KeyValuePair<string, string[]>(nation, ships.ToArray());
                }
                return result;
            }
        }

        private void TimingThreadStart()
        {
            while (true)
            {
                Thread.Sleep(SmallDelay);
                if (!m_stopWatch.IsRunning)
                    continue;
                lock (m_locker)
                {
                    double t = this.Time.TotalSeconds;
                    foreach (var helm in m_helms.Values)
                        ((Ship)helm.Ship).Dynamics.UpdateTime(t);
                    foreach (Missile missile in m_missiles)
                        missile.UpdateTime(t);
                    var deleted = m_missiles.Where(missile => missile.IsDead).ToList();
                    foreach (var missile in deleted)
                        m_missiles.Remove(missile);
                }
            }
        }

        static int generation = 0;

        public void BigBangTest()
        {
            lock (m_locker)
            {
                for (int i = 0; i < 100; i++)
                {
                    var h = Random.NextAngle();
                    var a = Random.NextDouble();
                    var classification = Catalog.Instance.ShipClasses.Values.First();
                    var missile = Catalog.Instance.MissileClasses.Values.First();
                    var helm = new HelmDefinition
                               {
                                   Acceleration = a * classification.MaximumAcceleration,
                                   AccelerateTo = a * classification.MaximumAcceleration,
                                   ClassName = classification.Name,
                                   Heading = h,
                                   HeadingTo = h,
                                   Nation = "Солярианская Лига",
                                   ShipName = "Бандит-" + (generation > 0 ? generation + "-" : "") + (i + 1),
                                   MissileNumber = 1,
                                   MissileName = missile.Name,
                                   Position = 300000000 * (Random.NextDouble() + Random.NextDouble()) * Random.NextDirection(),
                                   Speed = 300000 * (Random.NextDouble() + Random.NextDouble()) * Random.NextDirection(),
                               };
                    m_helms.Add(helm.ShipName, Helm.Load(helm));
                }
                for (int i = 0; i < 1000; i++)
                {
                    var h = Random.NextAngle();
                    var classification = Catalog.Instance.MissileClasses.Values.First();
                    var from = m_helms.Values.RandomOf(Random);
                    var to = m_helms.Values.RandomOf(Random);
                    var missile = new Missile(from.Ship, true, to.Ship, 1, Time);
                    m_missiles.Add(missile);
                }
                generation++;
            }
        }
    }
}
