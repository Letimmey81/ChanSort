﻿using System.IO;
using System.Text;
using ChanSort.Api;

namespace ChanSort.Loader.Panasonic
{
  public class PanasonicPlugin : ISerializerPlugin
  {
    public string DllName { get; set; }
    public string PluginName => "Panasonic (*.db, *.bin, *.xml)";
    public string FileFilter => "*.db;*.bin;*.xml";

    public SerializerBase CreateSerializer(string inputFile)
    {
      // check for files in the 2022 /mnt/vendor/tvdata/database/channel/ directory structure file format with tv.db and idtvChannel.bin
      var name = Path.GetFileName(inputFile).ToLowerInvariant();
      var baseDir = Path.GetDirectoryName(inputFile);
      if (name == "hotel.bin")
        baseDir = Path.Combine(baseDir, "mnt", "vendor", "tvdata", "database");
      if (name == "idtvChannel.bin")
        baseDir = Path.GetDirectoryName(baseDir);
      var tvDb = Path.Combine(baseDir, "tv.db");
      if (File.Exists(tvDb) && File.Exists(Path.Combine(baseDir, "channel", "idtvChannel.bin")))
        return new IdtvChannelSerializer(tvDb);

      // Android based models use an .xml format. Unfortunately that format is utter garbage and not really useful
      var ext = Path.GetExtension(inputFile).ToLowerInvariant();
      if (ext == ".xml")
      {
        var data = File.ReadAllBytes(inputFile);
        var header = Encoding.ASCII.GetBytes("<ChannelList>\n<ChannelInfo IsModified=");
        for (int i = 0; i < header.Length; i++)
        {
          if (data[i] != header[i])
            return null;
        }

        return new XmlSerializer(inputFile);
      }

      // old svl.db / svl.bin formats
      return new Serializer(inputFile);
    }
  }
}
