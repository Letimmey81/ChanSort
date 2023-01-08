﻿//#define Win10_TAR
#define GNU_TAR
//#define TestBuild

using System.Text;
using Microsoft.Data.Sqlite;
using ChanSort.Api;
#if Win10_TAR
using System.Diagnostics;
#elif GNU_TAR
#else
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers.Tar;
#endif

namespace ChanSort.Loader.TCL
{
  /*
   * This class loads TCL / Thomson channel lists from a directory or a .tar file containing cloneCRC.bin, DtvData.db and satellite.db.
   *
   * None of the sample files contained more than a single input source (DVB-C/T/S), so for the time being this loader puts everything into a single list
   */
  class DtvDataSerializer : SerializerBase
  {
    private readonly ChannelList channels = new (SignalSource.All, "All");
    private string dbDir;
    private string dtvFile;
    private string satFile;
    private string crcFile;

    private readonly HashSet<string> tableNames = new();
    private readonly StringBuilder protocol = new();

#if GNU_TAR
    private GnuTar gnuTar;
#endif

    #region ctor()
    public DtvDataSerializer(string inputFile) : base(inputFile)
    {
      this.Features.ChannelNameEdit = ChannelNameEditMode.All;
#if TestBuild
      this.Features.DeleteMode = DeleteMode.NotSupported;
#else
      this.Features.DeleteMode = DeleteMode.Physically;
#endif
      this.Features.CanSkipChannels = false;
      this.Features.CanLockChannels = false;
      this.Features.CanHideChannels = true;
      this.Features.FavoritesMode = FavoritesMode.Flags;
      this.Features.MaxFavoriteLists = 1;

      this.DataRoot.AddChannelList(this.channels);
      channels.VisibleColumnFieldNames.Remove(nameof(ChannelInfo.Skip));
      channels.VisibleColumnFieldNames.Remove(nameof(ChannelInfo.Encrypted));
      channels.VisibleColumnFieldNames.Remove(nameof(ChannelInfo.AudioPid));
      channels.VisibleColumnFieldNames.Remove(nameof(ChannelInfo.Provider));
      channels.VisibleColumnFieldNames.Add(nameof(ChannelInfo.Source));
    }
    #endregion

    #region GetDataFilePaths()
    public override IEnumerable<string> GetDataFilePaths()
    {
      var list = new List<string>();
      list.Add(this.FileName);
      var backupFile = GetBackupFilePath();
      if (File.Exists(backupFile))
        list.Add(backupFile);
      return list;
    }

    private string GetBackupFilePath()
    {
      var dir = Path.GetDirectoryName(this.FileName) ?? ".";
      var name = Path.GetFileNameWithoutExtension(this.FileName);
      var ext = Path.GetExtension(this.FileName);
      var backupFile = Path.Combine(dir, name + "Backup" + ext);
      return backupFile;
    }

    #endregion

    #region Load()
    public override void Load()
    {
      PrepareWorkingDirectory();
      ValidateCrc();
      ReadSattelliteDb();
      ReadDtvDataDb();
    }
    #endregion
    
    #region PrepareWorkingDirectory()
    /// <summary>
    /// this.FileName might be
    /// - a .tar file containing database/cloneCRC.bin, database/userdata/DtvData.db, database/userdata/satellite.db
    /// - a .db file in a folder with DtvData.db and satellite.db and a cloneCRC.bin in either the same dir or the parent dir
    /// Other situations have already been handled in the <see cref="TclPlugin"/>
    /// </summary>
    private void PrepareWorkingDirectory()
    {
      var ext = Path.GetExtension(this.FileName).ToLowerInvariant();
      if (ext == ".tar")
      {
        UntarToTempDir();
        this.crcFile = Path.Combine(this.TempPath, "database", "cloneCRC.bin");
        this.dbDir = Path.Combine(this.TempPath, "database", "userdata");
      }
      else if (ext == ".db")
      {
        this.dbDir = Path.GetDirectoryName(this.FileName);
        this.crcFile = Path.Combine(this.dbDir, "cloneCRC.bin");
        if (!File.Exists(crcFile))
          this.crcFile = Path.Combine(Path.GetDirectoryName(this.dbDir), "cloneCRC.bin");
      }
      else
        throw LoaderException.TryNext("unrecognized TCL/Thomson directory structure");

      this.dtvFile = Path.Combine(dbDir, "DtvData.db");
      if (!File.Exists(dtvFile))
        throw LoaderException.TryNext("Missing DtvData.db file");

      this.satFile = Path.Combine(dbDir, "satellite.db");
      if (!File.Exists(satFile))
        satFile = null;

      if (!File.Exists(crcFile))
        crcFile = null;
    }
    #endregion

    #region UntarToTempDir()
    private void UntarToTempDir()
    {
      this.TempPath = Path.Combine(Path.GetTempPath(), "ChanSort_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
      Directory.CreateDirectory(this.TempPath);

#if Win10_TAR
      var psi = new ProcessStartInfo("tar");
      psi.UseShellExecute = true;
      psi.WindowStyle = ProcessWindowStyle.Hidden;
      psi.WorkingDirectory = this.TempPath;
      psi.Arguments = $"xf \"{this.FileName}\"";
      var proc = Process.Start(psi);
      proc.WaitForExit();
#elif GNU_TAR
      this.gnuTar = new GnuTar();
      gnuTar.ExtractToDirectory(this.FileName, this.TempPath);
#else
      using var tar = TarArchive.Open(this.FileName);
      var rdr = tar.ExtractAllEntries();
      rdr.WriteAllToDirectory(this.TempPath, new ExtractionOptions { ExtractFullPath = true });
#endif
    }
    #endregion

    #region ValidateCrc()
    private void ValidateCrc()
    {
      if (!File.Exists(crcFile)) 
        return;

      var crcData = File.ReadAllBytes(crcFile);
      var crc = Crc16.CCITT;

      var data = File.ReadAllBytes(dtvFile);
      var actual = crc.Calc(data);
      var expected = BitConverter.ToUInt16(crcData, 2);
      if (actual != expected)
      {
        var msg = $"Invalid CRC16-CCITT check sum for {dtvFile}. Expected {expected:X4} but calculated {actual:X4}";
        protocol.AppendLine(msg);
        //throw LoaderException.Fail(msg);
      }

      if (satFile != null)
      {
        data = File.ReadAllBytes(satFile);
        actual = crc.Calc(data);
        expected = BitConverter.ToUInt16(crcData, 4);
        if (actual != expected)
        {
          var msg = $"Invalid CRC16-CCITT check sum for {satFile}. Expected {expected:X4} but calculated {actual:X4}";
          protocol.AppendLine(msg);
          //throw LoaderException.Fail(msg);
        }
      }
    }

    #endregion

    #region ReadSattelliteDb()
    private void ReadSattelliteDb()
    {
      if (this.satFile == null)
        return;
      string satConnString = $"Data Source={satFile};Pooling=False";
      using var conn = new SqliteConnection(satConnString);
      conn.Open();
      using var cmd = conn.CreateCommand();

      cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
      using (var r = cmd.ExecuteReader())
      {
        while (r.Read())
          this.tableNames.Add(r.GetString(0).ToLowerInvariant());
      }

      if (!this.tableNames.Contains("sateliteinfotbl") || !this.tableNames.Contains("transponderinfotbl"))
        throw LoaderException.TryNext("File doesn't contain the expected tables");

      this.ReadSatellites(cmd);
    }

    #endregion

    #region ReadDtvDataDb()
    private void ReadDtvDataDb()
    {
      string dtvConnString = $"Data Source={dtvFile};Pooling=False";
      using var conn = new SqliteConnection(dtvConnString);
      conn.Open();
      using var cmd = conn.CreateCommand();

      this.ReadTransponders(cmd);
      this.ReadChannels(cmd);
    }
    #endregion

    #region ReadSatellites()
    private void ReadSatellites(SqliteCommand cmd)
    {
      cmd.CommandText = "select SateliteID, SateliteName, Longitude from SateliteInfoTbl";
      using var r = cmd.ExecuteReader();
      while (r.Read())
      {
        Satellite sat = new Satellite(r.GetInt32(0));
        string eastWest = "E";
        int pos = r.IsDBNull(2) ? 0 : r.GetInt32(2);
        if (pos != 0)
        {
          if (pos < 0)
          {
            pos = -pos;
            eastWest = "W";
          }

          sat.OrbitalPosition = $"{pos / 100}.{pos % 100}{eastWest}";
        }

        sat.Name = r.GetString(1);
        this.DataRoot.AddSatellite(sat);
      }
    }
    #endregion

    #region ReadTransponders()
    private void ReadTransponders(SqliteCommand cmd)
    {
      //cmd.CommandText = "select TransponderId, SateliteId, Freq, Polarisation, SymbolRate from TransponderInfoTbl";
      cmd.CommandText = "select u16MuxTblID, SatTblID, Freq, null, SymbolRate, TransportStreamId, OriginalNetworkId from MuxInfoTbl";
      using var r = cmd.ExecuteReader();
      while (r.Read())
      {
        int id = r.GetInt32(0);
        int satId = r.IsDBNull(1) ? -1 : r.GetInt32(1);
        int freq = r.GetInt32(2);
        
        if (this.DataRoot.Transponder.TryGet(id) != null)
          continue;
        Transponder tp = new Transponder(id);
        tp.FrequencyInMhz = (decimal)freq / 1000;
        //tp.Polarity = r.GetInt32(3) == 0 ? 'H' : 'V';
        tp.Satellite = this.DataRoot.Satellites.TryGet(satId);
        tp.SymbolRate = r.GetInt32(4);
        tp.TransportStreamId = r.GetInt32(5);
        tp.OriginalNetworkId = r.GetInt32(6);
        this.DataRoot.AddTransponder(tp.Satellite, tp);
      }
    }
    #endregion

    #region ReadChannels()
    private void ReadChannels(SqliteCommand cmd)
    {
      cmd.CommandText = $@"
select 
  p.u32Index, p.ProgNum, p.ServiceName, p.ShortServiceName, p.ServiceID, p.VideoType, p.PCRPID, p.VideoPID, p.unlockedFlag, p.LCN, p.LCNAssignmentType, p.EditFlag,
  m.OriginalNetworkId, m.TransportStreamId, m.Freq, m.SymbolRate,
  c.RouteName
from ProgramInfoTbl p 
left outer join MuxInfoTbl m on m.u16MuxTblID=p.u16MuxTblID
left outer join CurCIOPSerType c on c.u8DtvRoute=p.u8DtvRoute
";

      using var r = cmd.ExecuteReader();
      while (r.Read())
      {
        var handle = r.GetInt32(0);
        var oldProgNr = r.GetInt32(1);
        if (oldProgNr == 65535)
          continue;

        var name = r.GetString(2)?.TrimEnd(' ', '\0');
        ChannelInfo channel = new ChannelInfo(0, handle, oldProgNr, name);
        channel.ShortName = r.GetString(3).TrimEnd(' ', '\0');
        channel.ServiceId = r.GetInt32(4);
        var vtype = r.GetInt32(5);
        channel.ServiceTypeName = vtype == 1 ? "SD-TV" : vtype == 4 ? "HD-TV" : vtype == 6 ? "UHD-TV" : null;
        channel.PcrPid = r.GetInt32(6);
        channel.VideoPid = r.GetInt32(7);
        channel.Hidden = r.GetBoolean(8);
        var edit = r.GetInt32(11);
        channel.Favorites = (edit & 0x01) != 0 ? Favorites.A : 0;
        channel.AddDebug($"LCN={r.GetValue(9)}, AT={r.GetValue(10)}, Edit={edit:x4}");

        // DVB
        var ixD = 12;
        var ixC = ixD + 4;
        if (!r.IsDBNull(ixD))
        {
          channel.OriginalNetworkId = r.GetInt32(ixD + 0);
          channel.TransportStreamId = r.GetInt32(ixD + 1);
          channel.FreqInMhz = (decimal) r.GetInt32(ixD + 2) / 1000;
          channel.SymbolRate = r.GetInt32(ixD + 3);
          if (channel.FreqInMhz > 10000)
            channel.FreqInMhz = (int) channel.FreqInMhz;
          channel.Source = r.GetString(ixC);
        }

        if (!channel.IsDeleted)
          this.DataRoot.AddChannel(this.channels, channel);
      }
    }
    #endregion


    #region Save()
    public override void Save()
    {
      string channelConnString = $"Data Source={dtvFile};Pooling=False";
      using (var conn = new SqliteConnection(channelConnString))
      {
        conn.Open();
        using var trans = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
#if TestBuild
        SqliteCommand cmd2 = null;
#else
        using var cmd2 = conn.CreateCommand();
#endif

        this.WriteChannels(cmd, cmd2, this.channels);
        trans.Commit();

        cmd.Transaction = null;
      }

      UpdateCrc();

      if (Path.GetExtension(this.FileName).ToLowerInvariant() == ".tar")
        WriteToTar();
    }
    #endregion

    #region WriteChannels()
    private void WriteChannels(SqliteCommand cmd, SqliteCommand cmdDelete, ChannelList channelList)
    {
      cmd.CommandText = "update PrograminfoTbl set ProgNum=@nr"
#if !TestBuild      
        + ", ServiceName=@name, unlockedFlag=@hide, EditFlag=(EditFlag & 0xFFFFFFFE) | @editflag"
#endif
        + " where u32Index=@handle";
      cmd.Parameters.Add("@handle", SqliteType.Integer);
      cmd.Parameters.Add("@nr", SqliteType.Integer);
#if !TestBuild
      cmd.Parameters.Add("@name", SqliteType.Blob, 64);
      cmd.Parameters.Add("@hide", SqliteType.Integer);
      cmd.Parameters.Add("@editflag", SqliteType.Integer);
#endif
      cmd.Prepare();

#if !TestBuild
      //cmdDelete.CommandText = @"delete from PrograminfoTbl where u32Index=@handle;";
      //cmdDelete.Parameters.Add("@handle", SqliteType.Integer);
      //cmdDelete.Prepare();
#endif

      foreach (ChannelInfo channel in channelList.Channels)
      {
        if (channel.IsProxy) // ignore reference list proxy channels
          continue;

        if (channel.IsDeleted)
        {
#if !TestBuild
          cmdDelete.Parameters["@handle"].Value = channel.RecordIndex;
          cmdDelete.ExecuteNonQuery();
#endif
        }
        else
        {
          channel.UpdateRawData();
          cmd.Parameters["@handle"].Value = channel.RecordIndex;
          cmd.Parameters["@nr"].Value = channel.NewProgramNr;
#if !TestBuild
          var bytes = Encoding.UTF8.GetBytes(channel.Name);
          var blob = new byte[64];
          Tools.MemCopy(bytes, 0, blob, 0, 64);
          cmd.Parameters["@name"].Value = blob;
          cmd.Parameters["@hide"].Value = channel.Hidden;
          cmd.Parameters["@editflag"].Value = channel.Favorites == 0 ? 0 : 0x0001;
#endif
          cmd.ExecuteNonQuery();
        }
      }
    }
    #endregion

    #region UpdateCrc
    /// <summary>
    /// update CRC in cloneCRC.bin
    /// </summary>
    private void UpdateCrc()
    {
      if (this.crcFile == null)
        return;

      var dtvData = File.ReadAllBytes(dtvFile);
      var crc = Crc16.CCITT.Calc(dtvData);
      var crcData = File.ReadAllBytes(this.crcFile);
      crcData[2] = (byte)(crc & 0xFF);
      crcData[3] = (byte)(crc >> 8);
      File.WriteAllBytes(crcFile, crcData);
    }
    #endregion

    #region WriteToTar()
    private void WriteToTar()
    {
      // delete old .tar file and create a new one from temp dir
      File.Delete(this.FileName);
#if Win10_TAR
      var psi = new ProcessStartInfo("tar");
      psi.UseShellExecute = true;
      psi.WindowStyle = ProcessWindowStyle.Hidden;
      psi.WorkingDirectory = this.TempPath;
      psi.Arguments = $"cf \"{this.FileName}\" *";
      var proc = Process.Start(psi);
      proc.WaitForExit();
#elif GNU_TAR
      this.gnuTar.UpdateFromDirectory(this.FileName);
#else
      using var tar = TarArchive.Create();
      tar.AddAllFromDirectory(this.TempPath);
      tar.SaveTo(this.FileName, new TarWriterOptions(CompressionType.None, true));
#endif
    }
    #endregion
  }
}
