﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Web;
using System.Xml.Serialization;

namespace NetworkManager
{
  public class VirtualDevice
  {
    /// <summary>
    /// This object is passed to NodeInitializer if you want to create a node inside the virtual network. Communications in the virtual network do not take place via the internet, but through a pipeline, simulating the Internet protocol.
    /// </summary>
    public VirtualDevice()
    {
      Id = _lastId + 1;
      _lastId = Id;
      MachineName = Environment.MachineName;
      Domain = Id + "." + MachineName.ToLower();
      Address = "vd://" + Domain;
      Ip = (uint)Domain.GetHashCode();
    }
    private Device.BaseDevice _device;
    internal Device.BaseDevice Device
    {
      get => _device;
      set
      {
        _device = value;
        if (MachineName != Environment.MachineName) return;
        try
        {
          _pipeServer = new NamedPipeServerStream(Ip.ToString(), PipeDirection.InOut);
        }
        catch
        {
          // ignored
        }

        new System.Threading.Thread(PipeReader).Start();
      }
    }
    private NamedPipeServerStream _pipeServer;
    private static int _lastId;
    private void PipeReader()
    {
      while (true)
      {
        //if (!_pipeServer.IsConnected)
        _pipeServer.WaitForConnection();
        var stream = new StreamString(_pipeServer);
        var xmlRequest = stream.ReadString();
        Converter.XmlToObject(xmlRequest, typeof(WebRequest), out var objRequest);
        var request = (WebRequest)objRequest;
        if (IsOnline)
        {
          var response = WebServer(request);
#if DEBUG
          if (response == null)
            System.Diagnostics.Debugger.Break();
#endif
          var xmlResponse = Converter.ObjectToXml(response);
          stream.WriteString(xmlResponse);
        }
        else
          stream.WriteString("");
        _pipeServer.Disconnect();
      }
    }
    public static WebResponse HttpRequest(WebRequest request)
    {
      var ip = ResolveDNS(request.Address, out var machineName);
      string xmlResponse;
      var xmlRequest = Converter.ObjectToXml(request);
      //lock (_pipeServer)
      using (var pipeClient = new NamedPipeClientStream(machineName, ip.ToString(), PipeDirection.InOut))
      {
        pipeClient.Connect();
        var stream = new StreamString(pipeClient);
        stream.WriteString(xmlRequest);
        xmlResponse = stream.ReadString();
        //pipeClient.Close();
      }
      Converter.XmlToObject(xmlResponse, typeof(WebResponse), out var objResponse);
      var response = (WebResponse)objResponse;
      return response;
    }
    public static uint ResolveDNS(string address, out string machineName)
    {
      var uriAddress = new Uri(address);
      var domain = uriAddress.Authority;
      machineName = domain.Split('.')[1];
      if (machineName.Equals(Environment.MachineName, StringComparison.InvariantCultureIgnoreCase)) machineName = ".";
      return (uint)domain.GetHashCode();
    }
    /// <summary>
    /// Returns the remote WebResponse content generated by the request
    /// </summary>
    /// <param name="request">Web request</param>
    /// <returns>Server response</returns>
    public WebResponse WebServer(WebRequest request)
    {
      {
        WebResponse response = null;
        if (!IsOnline) return null; //null if is offline
        _currentWebRequest += 1;
        if (_currentWebRequest > 5)
          response = new WebResponse(null, null, 429, "Too Many Requests");
        else
        {
          using (Stream stream = new MemoryStream())
            if (Device.OnReceivesHttpRequest(request.QueryString, request.Form, request.FromIp, out var contentType, stream))
            {
              var mb = stream.Length / 1048576f;
              // It is empirical but excellent for simulating the network speed as set by the Virtual Device
              var pauseMs = (int)(mb / NetSpeed * 1000 * _currentWebRequest);
              System.Threading.Thread.Sleep(pauseMs);
              stream.Position = 0;
              using (var streamReader = new StreamReader(stream))
                response = new WebResponse(streamReader.ReadToEnd(), contentType, 200, "OK");
            }
        }
        _currentWebRequest -= 1;
        return response; //null if is offline
      }
    }
    private int _currentWebRequest;
    public readonly string MachineName;
		/// <summary>
		/// Base 1 Id, Each virtual device has a progressive number in order of creation.
		/// </summary>
		public readonly int Id;
    public readonly string Domain;
    public readonly string Address;
    public readonly uint Ip;
    /// <summary>
    /// Set this value to "false" to disconnect the simulated connection to the virtual machine interner. 
    /// When this value is "true" then the connection to the virtual network simulates will work correctly.
    /// </summary>
    public bool IsOnline
    {
      get => NetSpeed > 0F;
      set { if (value) NetSpeed = _ns > 0F ? _ns : 1F; else _ns = NetSpeed; NetSpeed = 0F; }
    }
    private float _ns;
    /// <summary>
    /// Simulated internet speed in megabytes per second.
    /// Set this value to zero to simulate the disconnection of the virtual internet network.
    /// </summary>
    public float NetSpeed = 10;
  }
  public class WebResponse
  {
    public WebResponse()
    {
    }
    internal WebResponse(string text, string contentType, int statusCode, string statusDescription)
    {
      Text = text;
      Status.Code = statusCode;
      Status.Description = statusDescription;
      if (contentType != null)
        Headers.Add("Content-Type", contentType);
    }
    public string StatusMessage => Status.Code.ToString() + " " + Status.Description;
    [XmlElement] public ResponseStatus Status = new ResponseStatus();
    public class ResponseStatus
    {
      public int Code;
      public string Description;
    }
    [XmlElement] public string Text;

    [XmlIgnore] public NameValueCollection Headers => HeadersList.Aggregate(new NameValueCollection(), (a, b) => { a[b.Key] = b.Value; return a; });
    private void SetHeaders(NameValueCollection headers)
    {
      foreach (var key in headers.AllKeys) HeadersList.Add(new KeyValue() { Key = key, Value = headers[key] });
    }
    [XmlElement] public List<KeyValue> HeadersList = new List<KeyValue>();
    public class KeyValue
    {
      public string Key;
      public string Value;
    }

  }
  public class WebRequest
  {
    public WebRequest()
    {
    }
    public WebRequest(string address, NameValueCollection form, string method, string fromIp)
    {
      //SetQueryString(queryString);
      Address = address;
      SetForm(form);
      Method = method;
      FromIp = fromIp;
    }
    [XmlElement] public string Address;
    [XmlElement] public string FromIp;
    [XmlElement] public string Method;
    [XmlIgnore] public NameValueCollection QueryString => HttpUtility.ParseQueryString(new Uri(Address).Query);
    [XmlIgnore] public NameValueCollection Form => FormList.Aggregate(new NameValueCollection(), (a, b) => { a[b.Key] = b.Value; return a; });
    private void SetForm(NameValueCollection form)
    {
      foreach (var key in form.AllKeys) FormList.Add(new KeyValue() { Key = key, Value = form[key] });
    }
    [XmlElement] public List<KeyValue> FormList = new List<KeyValue>();
    public class KeyValue
    {
      public string Key;
      public string Value;
    }
  }
  public class StreamString
  {
    private readonly Stream _ioStream;
    private readonly UnicodeEncoding _streamEncoding;

    public StreamString(Stream ioStream)
    {
      _ioStream = ioStream;
      _streamEncoding = new UnicodeEncoding();
    }

    public string ReadString()
    {
      var bytes = new byte[4];
      _ioStream.Read(bytes, 0, 4);
      var len = Converter.BytesToInt(bytes);
      var inPipeline = new byte[len];
      _ioStream.Read(inPipeline, 0, len);
      return _streamEncoding.GetString(inPipeline);
    }

    public int WriteString(string outString)
    {
      var outPipeline = _streamEncoding.GetBytes(outString);
      var len = outPipeline.Length;
      var bytes = Converter.GetBytes(len);
      _ioStream.Write(bytes, 0, 4);
      _ioStream.Write(outPipeline, 0, len);
      _ioStream.Flush();
      return outPipeline.Length + 4;
    }
  }
}
