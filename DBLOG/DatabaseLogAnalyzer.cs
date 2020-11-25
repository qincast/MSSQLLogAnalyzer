﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DBLOG
{
    /// <summary>
    /// Summary: SQL Server Database Log Analyzer. Author: AP0405140
    /// <para></para> 
    /// <para>History:</para> 
    /// <para>2020/03/08 AP0405140 create.</para> 
    /// </summary>
    [Serializable]
    public class DatabaseLogAnalyzer
    {
        private string _objectname,    // 对象名
                       _starttime, _endtime, // 开始时间 结束时间
                       _startLSN, _endLSN, _endLSN_var, // 开始LSN 结束LSN
                       _tsql;
        private DatabaseOperation DB;  // 数据库操作对象
        /// <summary>
        /// Readed Percent (0-100).
        /// </summary>
        public int ReadPercent;       // 读取进度百分比 1-100

        /// <summary>
        /// Initializes a new instance of the DBLOG.DatabaseLogAnalyzer class.
        /// </summary>
        /// <param name="pservername">The name or network address of the instance of SQL Server to which to connect.</param>
        /// <param name="pdatabasename">The name of the database.</param>
        /// <param name="plogin">The SQL Server login account.</param>
        /// <param name="ppassword">The password for the SQL Server account logging on.</param>
        public DatabaseLogAnalyzer(string pservername, string pdatabasename, string plogin, string ppassword)
        {
            DB = new DatabaseOperation(pservername, pdatabasename, plogin, ppassword);
            DB.RefreshConnect();
        }

        /// <summary>
        /// Initializes a new instance of the DBLOG.DatabaseLogAnalyzer class.
        /// </summary>
        /// <param name="pconnectstring">The string used to open a SQL Server database.</param>
        public DatabaseLogAnalyzer(string pconnectstring)
        {
            DB = new DatabaseOperation(pconnectstring);
            DB.RefreshConnect();
        }

        /// <summary>
        /// Read database logs.
        /// </summary>
        /// <param name="pStartTime">Start Time</param>
        /// <param name="pEndTime">End Time</param>
        /// <param name="pObjectName">Table Name, Blank for query all objects.</param>
        /// <returns>DatabaseLog array.</returns>
        public DatabaseLog[] ReadLog(string pStartTime, string pEndTime, string pObjectName)
        {
            List<DatabaseLog> logs, dmllog, ddllog;

            _objectname = pObjectName ?? string.Empty;
            _objectname = (_objectname.Length > 0 && _objectname.Contains(".") == false ? "dbo." : "") + _objectname;
            _starttime = pStartTime;
            _endtime = pEndTime;

            logs = new List<DatabaseLog>();
            ReadPercent = 0;

            dmllog = ReadLogDML();
            logs.AddRange(dmllog);


            return logs.ToArray();
        }

        private List<DatabaseLog> ReadLogDML()
        {
            List<DatabaseLog> dmllog, tmplog;
            int i, readedlogrecords;
            string databasename, schemaname, tablename;
            DataTable dtLoglist, dtTables, dtTemp;
            DBLOG_DML[] tablelist;

            databasename = DB.DatabaseName;
            schemaname = "";
            tablename = "";
            if (_objectname.Length > 0)
            {
                schemaname = _objectname.Substring(0, _objectname.IndexOf(".", 0));
                tablename = _objectname.Substring(_objectname.IndexOf(".", 0) + 1, _objectname.Length - _objectname.IndexOf(".", 0) - 1);
            }

#if DEBUG
            _tsql = "if exists(select 1 from sys.tables where name=N'LogExplorer_AnalysisLog') drop table dbo.LogExplorer_AnalysisLog; ";
            DB.ExecuteSQL(_tsql, false);

            _tsql = @"create table dbo.LogExplorer_AnalysisLog
                      (LogID int identity(1,1) not null,
                       ADate datetime not null,
                       TableName nvarchar(280),
                       Logdescr varchar(6000),
                       Operation varchar(100),
                       LSN varchar(100)
                       constraint pk_LogExplorer_AnalysisLog primary key (LogID)) ";
            DB.ExecuteSQL(_tsql, false);

            // table for debug
            _tsql = @"if exists(select 1 from sys.tables where name=N'LogExplorer_temppagedata') drop table dbo.LogExplorer_temppagedata; 
                      create table dbo.LogExplorer_temppagedata([LSN] nvarchar(1000),
                                                                [PAGE ID] nvarchar(1000),
                                                                [AllocUnitId] nvarchar(1000),
                                                                ParentObject sysname,
                                                                Object sysname,
                                                                Field sysname,
                                                                Value nvarchar(max)); ";
            DB.ExecuteSQL(_tsql, false);
#endif

            // get DML Transaction list
            _tsql = "if object_id('tempdb..#TransactionList') is not null drop table #TransactionList; ";
            DB.ExecuteSQL(_tsql, false);

            _tsql = "select 'TransactionID'=a.[Transaction ID], "
                    + "     'BeginTime'=isnull(min(a.[Begin Time]),max(a.[End Time])), "
                    + "     'EndTime'=isnull(max(a.[End Time]),min(a.[Begin Time])), "
                    + "     'BeginLSN'=min([Current LSN]), "
                    + "     'EndLSN'=max([Current LSN]) "
                    + " into #TransactionList "
                    + " from sys.fn_dblog(null,null) a "
                    + " where a.[Transaction ID]<>N'0000:00000000' "
                    + " and exists(select 1 from sys.fn_dblog(null,null) b where b.[Transaction ID]=a.[Transaction ID] and b.Operation=N'LOP_COMMIT_XACT') "
                    + " group by a.[Transaction ID] "
                    + " having cast(min(a.[Begin Time]) as datetime) between '" + _starttime + "' and '" + _endtime + "' "
                    + " or cast(max(a.[End Time]) as datetime) between '" + _starttime + "' and '" + _endtime + "' ";
            DB.ExecuteSQL(_tsql, false);
            ReadPercent = ReadPercent + 5;

            // get StartLSN and EndLSN
            _tsql = "select 'StartLSN'=cast(cast(convert(varbinary,substring(t.StartLSN,1,8),2) as bigint) as varchar)+':'+cast(cast(convert(varbinary,substring(t.StartLSN,10,8),2) as bigint) as varchar)+':'+cast(cast(convert(varbinary,substring(t.StartLSN,19,4),2) as bigint) as varchar), "
                    + "     'EndLSN'=cast(cast(convert(varbinary,substring(t.EndLSN,1,8),2) as bigint) as varchar)+':'+cast(cast(convert(varbinary,substring(t.EndLSN,10,8),2) as bigint) as varchar)+':'+cast(cast(convert(varbinary,substring(t.EndLSN,19,4),2) as bigint) as varchar), "
                    + "     'EndLSNvar'=EndLSN "
                    + " from (select 'StartLSN'=cast(min(BeginLSN) as varchar),'EndLSN'=cast(max(EndLSN) as varchar) from #TransactionList) t ";
            dtTemp = DB.Query(_tsql, false);

            if (dtTemp != null && dtTemp.Rows.Count > 0)
            {
                _startLSN = "'" + dtTemp.Rows[0]["StartLSN"].ToString() + "'";
                _endLSN = "'" + dtTemp.Rows[0]["EndLSN"].ToString() + "'";
                _endLSN_var = "'" + dtTemp.Rows[0]["EndLSNvar"].ToString() + "'";
            }
            else
            {
                _startLSN = "null";
                _endLSN = "null";
                _endLSN_var = "";
            }

            // get DML original log list
            _tsql = "if object_id('tempdb..#LogList') is not null drop table #LogList; ";
            _tsql = _tsql + "select * into #LogList "
                    + " from sys.fn_dblog(" + _startLSN + ", " + _endLSN + ") t "
                    + " where [Transaction ID] in(select [TransactionID] from #TransactionList) "
                    + " and [Context] in('LCX_HEAP','LCX_CLUSTERED','LCX_MARK_AS_GHOST') "
                    + " and [Operation] in('LOP_INSERT_ROWS','LOP_DELETE_ROWS','LOP_MODIFY_ROW','LOP_MODIFY_COLUMNS') "
                    + " and [AllocUnitName]<>'Unknown Alloc Unit' "
                    + " and [AllocUnitName] not like 'sys.%' "
                    + " and [AllocUnitName] is not null "
                    + " and [AllocUnitName] not like '%LogExplorer_AnalysisLog%' ";

            if (_objectname.Length > 0)
            {
                _tsql = _tsql + " and case when parsename([AllocUnitName],3) is not null then parsename([AllocUnitName],2) else parsename([AllocUnitName],1) end='" + tablename + "' "
                              + " and case when parsename([AllocUnitName],3) is not null then parsename([AllocUnitName],3) else parsename([AllocUnitName],2) end='" + schemaname + "' ";
            }

            _tsql = _tsql 
                    + "union all "
                    + "select * "
                    + "   from sys.fn_dblog(null,null) t "
                    + "   where [Current LSN]>" + _endLSN_var
                    + "   and [Context] in('LCX_HEAP','LCX_CLUSTERED','LCX_MARK_AS_GHOST') "
                    + "   and [Operation] in('LOP_MODIFY_ROW','LOP_MODIFY_COLUMNS') "
                    + "   and [AllocUnitName]<>'Unknown Alloc Unit' "
                    + "   and [AllocUnitName] not like 'sys.%' "
                    + "   and [AllocUnitName] is not null "
                    + "   and [AllocUnitName] not like '%LogExplorer_AnalysisLog%' ";

            if (_objectname.Length > 0)
            {
                _tsql = _tsql + " and case when parsename([AllocUnitName],3) is not null then parsename([AllocUnitName],2) else parsename([AllocUnitName],1) end='" + tablename + "' "
                              + " and case when parsename([AllocUnitName],3) is not null then parsename([AllocUnitName],3) else parsename([AllocUnitName],2) end='" + schemaname + "' ";
            }
            DB.ExecuteSQL(_tsql, false);

            _tsql = _tsql
                     .Replace("if object_id('tempdb..#LogList') is not null drop table #LogList; ", "")
                     .Replace("select * into #LogList", "select *");
            dtLoglist = DB.Query(_tsql, false);

            // get table list
            _tsql = _tsql.Substring(0, _tsql.IndexOf("union all"));
            _tsql = _tsql.Replace("select * ",
                                  "select distinct 'TableName'=case when parsename([AllocUnitName],3) is not null then parsename([AllocUnitName],2) else parsename([AllocUnitName],1) end, 'SchemaName'=case when parsename([AllocUnitName],3) is not null then parsename([AllocUnitName],3) else parsename([AllocUnitName],2) end ");
            dtTables = DB.Query(_tsql, false);
            ReadPercent = ReadPercent + 5;

            tablelist = new DBLOG_DML[dtTables.Rows.Count];
            dmllog = new List<DatabaseLog>();
            i = 0;
            readedlogrecords = 0;
            foreach (DataRow dr in dtTables.Rows)
            {
                tablename = dr["TableName"].ToString();
                schemaname = dr["SchemaName"].ToString();
                tablelist[i] = new DBLOG_DML(databasename, schemaname, tablename, DB);

                _tsql = "[AllocUnitName] like '" + schemaname + "." + tablename + ".%' or [AllocUnitName]='" + schemaname + "." + tablename + "' ";
                tablelist[i].dtLogs = dtLoglist.Select(_tsql);

#if DEBUG
                _tsql = "insert into dbo.LogExplorer_AnalysisLog(ADate,TableName,Logdescr,Operation,LSN) "
                        + " select getdate(),'" + schemaname + "." + tablename + "', 'Start Analysis Log.', '','' ";
                DB.ExecuteSQL(_tsql, false);
#endif

                tmplog = tablelist[i].AnalyzeLog(_startLSN, _endLSN);
                dmllog.AddRange(tmplog);

                readedlogrecords = readedlogrecords + tablelist[i].dtLogs.Length;
                ReadPercent = ReadPercent + Convert.ToInt32((100.0 - ReadPercent * 1.0) * (readedlogrecords * 1.0 / dtLoglist.Rows.Count * 1.0));

#if DEBUG
                _tsql = "insert into dbo.LogExplorer_AnalysisLog(ADate,TableName,Logdescr,Operation,LSN) "
                        + " select getdate(),'" + schemaname + "." + tablename + "', 'End Analysis Log.', '','' ";
                DB.ExecuteSQL(_tsql, false);
#endif

                i = i + 1;
            }

            dmllog = dmllog.OrderBy(p => p.TransactionID).ToList();

            ReadPercent = 100;
            return dmllog;
        }
      
    }

    [Serializable]
    public class DatabaseLog
    {
        public string LSN { get; set; }
        public string Type { get; set; } // DML / DDL / DCL
        public string TransactionID { get; set; }
        public string BeginTime { get; set; }
        public string EndTime { get; set; }
        public string ObjectName { get; set; }
        public string Operation { get; set; }
        public string RedoSQL { get; set; }
        public string UndoSQL { get; set; }
        public string Message { get; set; }
    }
}