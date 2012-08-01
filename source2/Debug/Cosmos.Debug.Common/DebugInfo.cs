﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Data;
using System.Data.EntityClient;
using System.Data.SqlClient;
using System.Data.Common;
using System.Reflection;
using Microsoft.Win32;

namespace Cosmos.Debug.Common {
  public class DebugInfo : IDisposable {

    // Please beware this field, it may cause issues if used incorrectly.
    public static DebugInfo CurrentInstance { get; private set; }
    public class Field_Info {
      public string Type { get; set; }
      public int Offset { get; set; }
      public string Name { get; set; }
    }

    public class Field_Map {
      public string TypeName { get; set; }
      public List<string> FieldNames = new List<string>();
    }

    public class MLDebugSymbol {
      public string LabelName { get; set; }
      public int StackDifference { get; set; }
      public string AssemblyFile { get; set; }
      public int TypeToken { get; set; }
      public int MethodToken { get; set; }
      public int ILOffset { get; set; }
      public string MethodName { get; set; }
    }

    public class Local_Argument_Info {
      public bool IsArgument { get; set; }
      public bool IsArrayElement { get; set; }
      public string MethodLabelName { get; set; }
      public int Index { get; set; }
      public int Offset { get; set; }
      public string Name { get; set; }
      public string Type { get; set; }
    }

    protected SqlConnection mConnection;
    protected Entities mEntities;

    public DebugInfo(string aPathname, bool aCreate = false) {
      CurrentInstance = this;

      string xDbName = Path.GetFileNameWithoutExtension(aPathname);
      // Dont use DbConnectionStringBuilder class, it doesnt work with LocalDB properly.
      string xDataSouce = @"(LocalDB)\v11.0";
      //xDataSouce = @".\SQLEXPRESS";
      string xConnStr = @"Data Source=" + xDataSouce + ";Integrated Security=True;MultipleActiveResultSets=True;";

      if (aCreate) {
        using (var xConn = new SqlConnection(xConnStr)) {
          xConn.Open();

          bool xExists = false;
          using (var xCmd = xConn.CreateCommand()) {
            xCmd.CommandText = "select * from sys.databases where name = '" + xDbName + "'";
            using (var xReader = xCmd.ExecuteReader()) {
              xExists = xReader.Read();
            }
          }

          if (xExists) {
            // Yes this throws an exception if the database doesnt exist, so we have to
            // run it only if we know it exists.
            // This will detach and also delete the physica files.
            using (var xCmd = xConn.CreateCommand()) {
              xCmd.CommandText = "DROP DATABASE " + xDbName;
              xCmd.ExecuteNonQuery();
            }
          }
        }
      }

      // Initial Catalog is necessary for EDM
      xConnStr += "Initial Catalog=" + xDbName + ";AttachDbFilename=" + aPathname + ";";
      mConnection = new SqlConnection(xConnStr);

      var xWorkspace = new System.Data.Metadata.Edm.MetadataWorkspace(
        new string[] { "res://*/" }, new Assembly[] { Assembly.GetExecutingAssembly() });
      var xEntConn = new EntityConnection(xWorkspace, mConnection);
      mEntities = new Entities(xEntConn);

      // Do not open mConnection before mEntities.CreateDatabase
      if (aCreate) {
        // DatabaseExists checks if the DBName exists, not physical files.
        if (!mEntities.DatabaseExists()) {
          mEntities.CreateDatabase();
        }
      }
      mConnection.Open();
    }

    protected List<string> local_MappingTypeNames = new List<string>();
    public void WriteFieldMappingToFile(IEnumerable<Field_Map> aMapping) {
      IEnumerable<Field_Map> xMaps = aMapping.Where(delegate(Field_Map mp) {
        if (local_MappingTypeNames.Contains(mp.TypeName)) {
          return false;
        } else {
          local_MappingTypeNames.Add(mp.TypeName);
          return true;
        }
      });

      var xTx = mConnection.BeginTransaction(); 
      try {
        using (var xCmd = mConnection.CreateCommand()) {
          xCmd.Transaction = xTx;
          xCmd.CommandText = "INSERT INTO FIELD_MAPPING (ID, TYPE_NAME, FIELD_NAME)" +
                               " VALUES (NEWID(), @TYPE_NAME, @FIELD_NAME)";
          xCmd.Parameters.Add("@TYPE_NAME", SqlDbType.NVarChar);
          xCmd.Parameters.Add("@FIELD_NAME", SqlDbType.NVarChar);
          // Is a real DB now, but we still store all in RAM. We don't need to. Need to change to query DB as needed instead.
          foreach (var xItem in xMaps) {
            xCmd.Parameters[0].Value = xItem.TypeName;
            foreach (var xFieldName in xItem.FieldNames) {
              xCmd.Parameters[1].Value = xFieldName;
              xCmd.ExecuteNonQuery();
            }
          }
        }
        xTx.Commit();
      } catch (Exception) {
        xTx.Rollback();
        throw;
      }
    }

    public Field_Map GetFieldMap(string name) {
      var mp = new Field_Map();
      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "select TYPE_NAME, FIELD_NAME from FIELD_MAPPING where(TYPE_NAME='" + name + "')";
        using (var xReader = xCmd.ExecuteReader()) {
          mp.TypeName = name;
          while (xReader.Read()) {
            mp.FieldNames.Add(xReader.GetString(1));
          }
        }
      }
      return mp;
    }

    public void ReadFieldMappingList(List<Field_Map> aSymbols) {
      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "select TYPE_NAME, FIELD_NAME from FIELD_MAPPING order by TYPE_NAME";
        using (var xReader = xCmd.ExecuteReader()) {
          var mp = new Field_Map();
          while (xReader.Read()) {
            string xTypeName = xReader.GetString(0);
            if (xTypeName != mp.TypeName) {
              if (mp.FieldNames.Count > 0) {
                aSymbols.Add(mp);
              }
              mp = new Field_Map();
              mp.TypeName = xTypeName;
            }
            mp.FieldNames.Add(xReader.GetString(1));
          }
          aSymbols.Add(mp);
        }
      }
    }

    protected List<string> local_FieldInfoNames = new List<string>();
    public void WriteFieldInfoToFile(IEnumerable<Field_Info> aFields) {
      IEnumerable<Field_Info> xFields = aFields.Where(delegate(Field_Info mp) {
        if (local_FieldInfoNames.Contains(mp.Name)) {
          return false;
        } else {
          local_FieldInfoNames.Add(mp.Name);
          return true;
        }
      });

      var xTx = mConnection.BeginTransaction(); 
      try {
        using (var xCmd = mConnection.CreateCommand()) {
          xCmd.Transaction = xTx;
          xCmd.CommandText = "INSERT INTO FIELD_INFO (ID, TYPE, OFFSET, NAME)" +
                             " VALUES (NEWID(), @TYPE, @OFFSET, @NAME)";
          xCmd.Parameters.Add("@TYPE", SqlDbType.NVarChar);
          xCmd.Parameters.Add("@OFFSET", SqlDbType.Int);
          xCmd.Parameters.Add("@NAME", SqlDbType.NVarChar);
          // Is a real DB now, but we still store all in RAM. We don't need to. Need to change to query DB as needed instead.
          foreach (var xItem in xFields) {
            xCmd.Parameters[0].Value = xItem.Type;
            xCmd.Parameters[1].Value = xItem.Offset;
            xCmd.Parameters[2].Value = xItem.Name;
            xCmd.ExecuteNonQuery();
          }
        }
        xTx.Commit();
      } catch (Exception) {
        xTx.Rollback();
        throw;
      }
    }

    public Field_Info GetFieldInfo(string name) {
      var inf = new Field_Info();
      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "select TYPE, OFFSET, NAME from FIELD_INFO where NAME='" + name + "'";
        using (var xReader = xCmd.ExecuteReader()) {
          xReader.Read();
          inf.Type = xReader.GetString(0);
          inf.Offset = xReader.GetInt32(1);
          inf.Name = xReader.GetString(2);
        }
      }
      return inf;
    }

    public void ReadFieldInfoList(List<Field_Info> aSymbols) {
      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "select TYPE, OFFSET, NAME from FIELD_INFO";
        using (var xReader = xCmd.ExecuteReader()) {
          while (xReader.Read()) {
            aSymbols.Add(new Field_Info {
              Type = xReader.GetString(0),
              Offset = xReader.GetInt32(1),
              Name = xReader.GetString(2),
            });
          }
        }
      }
    }

    public void WriteSymbolsListToFile(IEnumerable<MLDebugSymbol> aSymbols) {
      var xTx = mConnection.BeginTransaction(); 
      try {
        using (var xCmd = mConnection.CreateCommand()) {
          xCmd.Transaction = xTx;
          xCmd.CommandText = "INSERT INTO MLSYMBOLs (ID, LABELNAME, STACKDIFF, ILASMFILE, TYPETOKEN, METHODTOKEN, ILOFFSET, METHODNAME)" +
                       " VALUES (NEWID(), @LABELNAME, @STACKDIFF, @ILASMFILE, @TYPETOKEN, @METHODTOKEN, @ILOFFSET, @METHODNAME)";
          xCmd.Parameters.Add("@LABELNAME", SqlDbType.NVarChar);
          xCmd.Parameters.Add("@STACKDIFF", SqlDbType.Int);
          xCmd.Parameters.Add("@ILASMFILE", SqlDbType.NVarChar);
          xCmd.Parameters.Add("@TYPETOKEN", SqlDbType.Int);
          xCmd.Parameters.Add("@METHODTOKEN", SqlDbType.Int);
          xCmd.Parameters.Add("@ILOFFSET", SqlDbType.Int);
          xCmd.Parameters.Add("@METHODNAME", SqlDbType.NVarChar);
          // Is a real DB now, but we still store all in RAM. We dont need to. Need to change to query DB as needed instead.
          foreach (var xItem in aSymbols) {
            xCmd.Parameters[0].Value = xItem.LabelName;
            xCmd.Parameters[1].Value = xItem.StackDifference;
            xCmd.Parameters[2].Value = xItem.AssemblyFile;
            xCmd.Parameters[3].Value = xItem.TypeToken;
            xCmd.Parameters[4].Value = xItem.MethodToken;
            xCmd.Parameters[5].Value = xItem.ILOffset;
            xCmd.Parameters[6].Value = xItem.MethodName;
            xCmd.ExecuteNonQuery();
          }
        }
        xTx.Commit();
      } catch (Exception) {
        xTx.Rollback();
        throw;
      }
    }

    public void ReadSymbolsList(List<MLDebugSymbol> aSymbols) {
      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "select LABELNAME, STACKDIFF, ILASMFILE, TYPETOKEN, METHODTOKEN, ILOFFSET, METHODNAME from MLSYMBOLs";
        using (var xReader = xCmd.ExecuteReader()) {
          while (xReader.Read()) {
            aSymbols.Add(new MLDebugSymbol {
              LabelName = xReader.GetString(0),
              StackDifference = xReader.GetInt32(1),
              AssemblyFile = xReader.GetString(2),
              TypeToken = xReader.GetInt32(3),
              MethodToken = xReader.GetInt32(4),
              ILOffset = xReader.GetInt32(5),
              MethodName = xReader.GetString(6)
            });
          }
        }
      }
    }

    public MLDebugSymbol ReadSymbolByLabelName(string labelName) {
      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "select LABELNAME, STACKDIFF, ILASMFILE, TYPETOKEN, METHODTOKEN, ILOFFSET, METHODNAME from MLSYMBOLs"
            + " WHERE LABELNAME = '" + labelName + "'";
        using (var xReader = xCmd.ExecuteReader()) {
          if (xReader.Read()) {
            return new MLDebugSymbol {
              LabelName = xReader.GetString(0),
              StackDifference = xReader.GetInt32(1),
              AssemblyFile = xReader.GetString(2),
              TypeToken = xReader.GetInt32(3),
              MethodToken = xReader.GetInt32(4),
              ILOffset = xReader.GetInt32(5),
              MethodName = xReader.GetString(6)
            };
          } else {
            return null;
          }
        }
      }
    }

    // tuple format: MethodLabel, IsArgument, Index, Offset
    public void WriteAllLocalsArgumentsInfos(IEnumerable<Local_Argument_Info> infos) {
      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "insert into LOCAL_ARGUMENT_INFO (ID, METHODLABELNAME, ISARGUMENT, INDEXINMETHOD, OFFSET, NAME, TYPENAME) values (NEWID(), @METHODLABELNAME, @ISARGUMENT, @INDEXINMETHOD, @OFFSET, @NAME, @TYPENAME)";
        xCmd.Parameters.Add("@METHODLABELNAME", SqlDbType.NVarChar);
        xCmd.Parameters.Add("@ISARGUMENT", SqlDbType.SmallInt);
        xCmd.Parameters.Add("@INDEXINMETHOD", SqlDbType.Int);
        xCmd.Parameters.Add("@OFFSET", SqlDbType.Int);
        xCmd.Parameters.Add("@NAME", SqlDbType.NVarChar);
        xCmd.Parameters.Add("@TYPENAME", SqlDbType.NVarChar);
        foreach (var xInfo in infos) {
          xCmd.Parameters[0].Value = xInfo.MethodLabelName;
          xCmd.Parameters[1].Value = xInfo.IsArgument ? 1 : 0;
          xCmd.Parameters[2].Value = xInfo.Index;
          xCmd.Parameters[3].Value = xInfo.Offset;
          xCmd.Parameters[4].Value = xInfo.Name;
          xCmd.Parameters[5].Value = xInfo.Type;
          xCmd.ExecuteNonQuery();
        }
      }
    }

    public IList<Local_Argument_Info> ReadAllLocalsArgumentsInfos() {
      var xTx = mConnection.BeginTransaction(); 
      try {
        using (var xCmd = mConnection.CreateCommand()) {
          xCmd.Transaction = xTx;
          xCmd.CommandText = "select METHODLABELNAME, ISARGUMENT, INDEXINMETHOD, OFFSET, NAME, TYPENAME from LOCAL_ARGUMENT_INFO";
          using (var xReader = xCmd.ExecuteReader()) {
            var xResult = new List<Local_Argument_Info>(xReader.RecordsAffected);
            while (xReader.Read()) {
              xResult.Add(new Local_Argument_Info {
                MethodLabelName = xReader.GetString(0),
                IsArgument = xReader.GetInt16(1) == 1,
                Index = xReader.GetInt32(2),
                Offset = xReader.GetInt32(3),
                Name = xReader.GetString(4),
                Type = xReader.GetString(5)
              });
            }
            return xResult;
          }
        }
        xTx.Commit();
      } catch (Exception) {
        xTx.Rollback();
        throw;
      }
    }

    public IList<Local_Argument_Info> ReadAllLocalsArgumentsInfosByMethodLabelName(string methodLabelName) {
      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "select METHODLABELNAME, ISARGUMENT, INDEXINMETHOD, OFFSET, NAME, TYPENAME from LOCAL_ARGUMENT_INFO"
            + " WHERE METHODLABELNAME = '" + methodLabelName + "'";
        using (var xReader = xCmd.ExecuteReader()) {
          var xResult = new List<Local_Argument_Info>();
          while (xReader.Read()) {
            xResult.Add(new Local_Argument_Info {
              MethodLabelName = xReader.GetString(0),
              IsArgument = xReader.GetInt16(1) == 1,
              Index = xReader.GetInt32(2),
              Offset = xReader.GetInt32(3),
              Name = xReader.GetString(4),
              Type = xReader.GetString(5)
            });
          }
          return xResult;
        }
      }
    }

    public void ReadLabels(out List<KeyValuePair<uint, string>> oLabels, out IDictionary<string, uint> oLabelAddressMappings) {
      oLabels = new List<KeyValuePair<uint, string>>();
      oLabelAddressMappings = new Dictionary<string, uint>();
      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "select LABELNAME, ADDRESS from Labels";
        using (var xReader = xCmd.ExecuteReader()) {
          while (xReader.Read()) {
            oLabels.Add(new KeyValuePair<uint, string>((uint)xReader.GetInt64(1), xReader.GetString(0)));
            oLabelAddressMappings.Add(xReader.GetString(0), (uint)xReader.GetInt64(1));
          }
        }
      }
    }

    // This is a heck of a lot easier than using sequences
    protected int mMethodId = 0;
    public int AddMethod(string aLabelPrefix) {
      mMethodId++;

      using (var xCmd = mConnection.CreateCommand()) {
        xCmd.CommandText = "INSERT INTO Methods (ID, MethodId, LabelPrefix) values (NEWID(), @MethodId, @LabelPrefix)";
        xCmd.Parameters.AddWithValue("@MethodId", mMethodId);
        xCmd.Parameters.AddWithValue("@LabelPrefix", aLabelPrefix);
        xCmd.ExecuteNonQuery();
      }

      return mMethodId;
    }

    public void WriteLabels(List<KeyValuePair<uint, string>> aMap) {
      var xTx = mConnection.BeginTransaction(); 
      try {
        using (var xCmd = mConnection.CreateCommand()) {
          xCmd.Transaction = xTx;
          xCmd.CommandText = "insert into Labels (ID, LABELNAME, ADDRESS) values (NEWID(), @LABELNAME, @ADDRESS)";
          xCmd.Parameters.Add("@LABELNAME", SqlDbType.NVarChar);
          xCmd.Parameters.Add("@ADDRESS", SqlDbType.BigInt);
          foreach (var xItem in aMap) {
            xCmd.Parameters[0].Value = xItem.Value;
            xCmd.Parameters[1].Value = xItem.Key;
            xCmd.ExecuteNonQuery();
          }
        }
        xTx.Commit();
      } catch (Exception) {
        xTx.Rollback();
        throw;
      }
    }

    public void Dispose() {
      if (mConnection != null) {
        var xConn = mConnection;
        mConnection = null;
        xConn.Close();
        // Dont set to null... causes problems because of bad code :(
        // Need to fix the whole class, but its here for now.
        //CurrentInstance = null;

        // Why do we have this? 
        // Dont remove though - when removed we cant run Cosmos stuff in hive for some reason?
        GC.SuppressFinalize(this);
      }
    }
  }

}