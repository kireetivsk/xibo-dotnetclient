/*
 * Xibo - Digitial Signage - http://www.xibo.org.uk
 * Copyright (C) 2006-2012 Daniel Garner
 *
 * This file is part of Xibo.
 *
 * Xibo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * any later version. 
 *
 * Xibo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with Xibo.  If not, see <http://www.gnu.org/licenses/>.
 */
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Diagnostics;
using XiboClient.Properties;

namespace XiboClient
{
    /// <summary>
    /// Layout Region, container for Media
    /// </summary>
    class Region : Panel
    {
        private BlackList _blackList;
        public delegate void DurationElapsedDelegate();
        public event DurationElapsedDelegate DurationElapsedEvent;

        private Media _media;
        private RegionOptions _options;
        public bool _hasExpired = false;
        public bool _layoutExpired = false;
        private int _currentSequence = -1;

        // Stat objects
        private StatLog _statLog;
        private Stat _stat;

        // Cache Manager
        private CacheManager _cacheManager;

        /// <summary>
        /// Creates the Region
        /// </summary>
        /// <param name="statLog"></param>
        /// <param name="cacheManager"></param>
        public Region(ref StatLog statLog, ref CacheManager cacheManager)
        {
            // Store the statLog
            _statLog = statLog;

            // Store the cache manager
            _cacheManager = cacheManager;

            //default options
            _options.width = 1024;
            _options.height = 768;
            _options.left = 0;
            _options.top = 0;
            _options.uri = null;

            this.Location = new System.Drawing.Point(_options.left, _options.top);
            this.Size = new System.Drawing.Size(_options.width, _options.height);
            this.BackColor = System.Drawing.Color.Transparent;

            if (Settings.Default.DoubleBuffering)
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
                SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            }

            // Create a new BlackList for us to use
            _blackList = new BlackList();
        }

        /// <summary>
        /// Options for the region
        /// </summary>
        public RegionOptions regionOptions
        {
            get 
            { 
                return this._options; 
            }
            set 
            { 
                this._options = value;

                EvalOptions();
            }
        }

        ///<summary>
        /// Evaulates the change in options
        ///</summary>
        private void EvalOptions() 
        {
            // First time
            bool initialMedia = (_currentSequence == -1);

            if (initialMedia)
            {
                // Evaluate the width, etc
                this.Location = new System.Drawing.Point(_options.left, _options.top);
                this.Size = new System.Drawing.Size(_options.width, _options.height);
            }

            // The idea here is to store the current media, start the new media and then replace
            Media currentMedia;
            Media newMedia = _media;

            // Loop around trying to set the next media
            bool setSuccessful = false;

            while (!setSuccessful)
            {
                // Store the current sequence
                int temp = _currentSequence;

                // Set the next media node for this panel
                if (!SetNextMediaNodeInOptions())
                {
                    // For some reason we cannot set a media node... so we need this region to become invalid
                    _hasExpired = true;
                    DurationElapsedEvent();
                    return;
                }

                // If the sequence hasnt been changed, OR the layout has been expired
                if (_currentSequence == temp || _layoutExpired)
                {
                    //there has been no change to the sequence, therefore the media we have already created is still valid
                    //or this media has actually been destroyed and we are working out way out the call stack
                    return;
                }

                // See if we can start the new media object
                try
                {
                    newMedia = CreateNextMediaNode(_options);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(new LogMessage("Region - Eval Options", "Unable to start new media object: " + ex.Message), LogType.Error.ToString());
                }

                // We have set a new media object.
                setSuccessful = true;
            }

            // First thing we do is stop the current stat record
            if (!initialMedia)
                CloseCurrentStatRecord();

            // Now we have newMedia and current media.
            currentMedia = _media;

            // Swap the media reference
            _media = newMedia;

            // Start the new media
            StartMedia(_media);

            // Remove the old media
            if (!initialMedia)
                StopMedia(currentMedia);

            // Open a stat record
            OpenStatRecordForMedia();
        }

        /// <summary>
        /// Sets the next media node. Should be used either from a mediaComplete event, or an options reset from 
        /// the parent.
        /// </summary>
        private bool SetNextMediaNodeInOptions()
        {
            int playingSequence = _currentSequence;

            // What if there are no media nodes?
            if (_options.mediaNodes.Count == 0)
            {
                Trace.WriteLine(new LogMessage("Region - SetNextMediaNode", "No media nodes to display"), LogType.Audit.ToString());

                return false;
            }

            // Zero out the options that are persisted
            _options.text = "";
            _options.documentTemplate = "";
            _options.copyrightNotice = "";
            _options.scrollSpeed = 30;
            _options.updateInterval = 6;
            _options.uri = "";
            _options.direction = "none";
            _options.javaScript = "";
            _options.Dictionary = new MediaDictionary();

            // Get a media node
            bool validNode = false;
            int numAttempts = 0;
            
            // Loop through all the nodes in order
            while (numAttempts < _options.mediaNodes.Count)
            {
                // Move the sequence on
                _currentSequence++;

                if (_currentSequence >= _options.mediaNodes.Count)
                {
                    // Start from the beginning
                    _currentSequence = 0;

                    // We have expired (want to raise an expired event to the parent)
                    _hasExpired = true;

                    Trace.WriteLine(new LogMessage("Region - SetNextMediaNode", "Media Expired:" + _options.ToString() + " . Reached the end of the sequence. Starting from the beginning."), LogType.Audit.ToString());

                    // Region Expired
                    DurationElapsedEvent();

                    // We want to continue on to show the next media (unless the duration elapsed event triggers a region change)
                    if (_layoutExpired)
                        return true;
                }

                // Get the media node for this sequence
                XmlNode mediaNode = _options.mediaNodes[_currentSequence];
                XmlAttributeCollection nodeAttributes = mediaNode.Attributes;

                // Set the media id
                if (nodeAttributes["id"].Value != null) 
                    _options.mediaid = nodeAttributes["id"].Value;

                // Check isnt blacklisted
                if (_blackList.BlackListed(_options.mediaid))
                {
                    Trace.WriteLine(new LogMessage("Region - SetNextMediaNode", string.Format("MediaID [{0}] has been blacklisted.", _options.mediaid)), LogType.Error.ToString());

                    // Carry on
                    continue;
                }

                // Assume we have a valid node at this point
                validNode = true;

                // Parse the options for this media node
                ParseOptionsForMediaNode(mediaNode, nodeAttributes);

                // Is this a file based media node?
                if (_options.type == "video" || _options.type == "flash" || _options.type == "image" || _options.type == "powerpoint")
                {
                    // Use the cache manager to determine if the file is valid
                    validNode = _cacheManager.IsValidPath(_options.uri);
                }

                // If we have a valid node, break out of the loop
                if (validNode)
                    break;

                // Increment the number of attempts and try again
                numAttempts++;
            }

            // If we dont have a valid node out of all the nodes in the region, then return false.
            if (!validNode)
                return false;

            Trace.WriteLine(new LogMessage("Region - SetNextMediaNode", "New media detected " + _options.type), LogType.Audit.ToString());

            return true;
        }

        /// <summary>
        /// Parse options for the media node
        /// </summary>
        /// <param name="mediaNode"></param>
        /// <param name="nodeAttributes"></param>
        private void ParseOptionsForMediaNode(XmlNode mediaNode, XmlAttributeCollection nodeAttributes)
        {
            // New version has a different schema - the right way to do it would be to pass the <options> and <raw> nodes to 
            // the relevant media class - however I dont feel like engineering such a change so the alternative is to
            // parse all the possible media type nodes here.

            // Type and Duration will always be on the media node
            _options.type = nodeAttributes["type"].Value;

            //TODO: Check the type of node we have, and make sure it is supported.

            if (nodeAttributes["duration"].Value != "")
            {
                _options.duration = int.Parse(nodeAttributes["duration"].Value);
            }
            else
            {
                _options.duration = 60;
                Trace.WriteLine("Duration is Empty, using a default of 60.", "Region - SetNextMediaNode");
            }

            // We cannot have a 0 duration here... not sure why we would... but
            if (_options.duration == 0 && _options.type != "video")
            {
                int emptyLayoutDuration = int.Parse(Properties.Settings.Default.emptyLayoutDuration.ToString());
                _options.duration = (emptyLayoutDuration == 0) ? 10 : emptyLayoutDuration;
            }

            // There will be some stuff on option nodes
            XmlNode optionNode = mediaNode.FirstChild;

            // Loop through each option node
            foreach (XmlNode option in optionNode.ChildNodes)
            {
                if (option.Name == "direction")
                {
                    _options.direction = option.InnerText;
                }
                else if (option.Name == "uri")
                {
                    _options.uri = option.InnerText;
                }
                else if (option.Name == "copyright")
                {
                    _options.copyrightNotice = option.InnerText;
                }
                else if (option.Name == "scrollSpeed")
                {
                    try
                    {
                        _options.scrollSpeed = int.Parse(option.InnerText);
                    }
                    catch
                    {
                        System.Diagnostics.Trace.WriteLine("Non integer scrollSpeed in XLF", "Region - SetNextMediaNode");
                    }
                }
                else if (option.Name == "updateInterval")
                {
                    try
                    {
                        _options.updateInterval = int.Parse(option.InnerText);
                    }
                    catch
                    {
                        System.Diagnostics.Trace.WriteLine("Non integer updateInterval in XLF", "Region - SetNextMediaNode");
                    }
                }

                // Add this to the options object
                _options.Dictionary.Add(option.Name, option.InnerText);
            }

            // And some stuff on Raw nodes
            XmlNode rawNode = mediaNode.LastChild;

            foreach (XmlNode raw in rawNode.ChildNodes)
            {
                if (raw.Name == "text")
                {
                    _options.text = raw.InnerText;
                }
                else if (raw.Name == "template")
                {
                    _options.documentTemplate = raw.InnerText;
                }
                else if (raw.Name == "embedHtml")
                {
                    _options.text = raw.InnerText;
                }
                else if (raw.Name == "embedScript")
                {
                    _options.javaScript = raw.InnerText;
                }
            }
        }

        /// <summary>
        /// Create the next media node based on the provided options
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private Media CreateNextMediaNode(RegionOptions options)
        {
            Media media;

            Trace.WriteLine(new LogMessage("Region - CreateNextMediaNode", string.Format("Creating new media: {0}, {1}", options.type, options.mediaid)), LogType.Audit.ToString());

            switch (options.type)
            {
                case "image":
                    options.uri = Settings.Default.LibraryPath + @"\" + options.uri;
                    media = new ImagePosition(options);
                    break;

                case "text":
                    media = new Text(options);
                    break;

                case "powerpoint":
                    options.uri = Settings.Default.LibraryPath + @"\" + options.uri;
                    media = new WebContent(options);
                    break;

                case "video":
                    options.uri = Settings.Default.LibraryPath + @"\" + options.uri;
                    media = new Video(options);
                    break;

                case "webpage":
                    media = new WebContent(options);
                    break;

                case "flash":
                    options.uri = Settings.Default.LibraryPath + @"\" + options.uri;
                    media = new Flash(options);
                    break;

                case "ticker":
                    media = new Rss(options);
                    break;

                case "embedded":
                    media = new Text(options);
                    break;

                case "datasetview":
                    media = new DataSetView(options);
                    break;

                case "shellcommand":
                    media = new ShellCommand(options);
                    break;

                default:
                    throw new InvalidOperationException("Not a valid media node type: " + options.type);
            }

            // Sets up the timer for this media
            media.Duration = options.duration;

            // Add event handler for when this completes
            media.DurationElapsedEvent += new Media.DurationElapsedDelegate(media_DurationElapsedEvent);

            return media;
        }

        /// <summary>
        /// Start the provided media
        /// </summary>
        /// <param name="media"></param>
        private void StartMedia(Media media)
        {
            media.RenderMedia();

            Trace.WriteLine(new LogMessage("Region - StartMedia", "Starting media"), LogType.Audit.ToString());

            Controls.Add(media);
        }

        /// <summary>
        /// Stop the provided media
        /// </summary>
        private void StopMedia(Media media)
        {
            Trace.WriteLine(new LogMessage("Region - Stop Media", "Stopping media"), LogType.Audit.ToString());

            // Hide the media
            media.Hide();

            // Remove the controls
            Controls.Remove(media);

            // Dispose of the current media
            try
            {
                // Dispose of the media
                media.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("No media to remove");
                Debug.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// Opens a stat record for the current media
        /// </summary>
        private void OpenStatRecordForMedia()
        {
            // This media has started and is being replaced
            _stat = new Stat();
            _stat.type = StatType.Media;
            _stat.fromDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _stat.scheduleID = _options.scheduleId;
            _stat.layoutID = _options.layoutId;
            _stat.mediaID = _options.mediaid;
        }

        /// <summary>
        /// Close out the stat record
        /// </summary>
        private void CloseCurrentStatRecord()
        {
            try
            {
                // Here we say that this media is expired
                _stat.toDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                // Record this stat event in the statLog object
                _statLog.RecordStat(_stat);
            }
            catch
            {
                Trace.WriteLine(new LogMessage("Region - StopMedia", "No Stat record when one was expected"), LogType.Error.ToString());
            }
        }

        /// <summary>
        /// The media has elapsed
        /// </summary>
        private void media_DurationElapsedEvent()
        {
            Trace.WriteLine(new LogMessage("Region - DurationElapsedEvent", string.Format("Media Elapsed: {0}", _options.uri)), LogType.Audit.ToString());

            // make some decisions about what to do next
            EvalOptions();
        }

        /// <summary>
        /// Clears the Region of anything that it shouldnt still have... 
        /// </summary>
        public void Clear()
        {
            try
            {
                // What happens if we are disposing this region but we have not yet completed the stat event?
                if (string.IsNullOrEmpty(_stat.toDate))
                {
                    // Say that this media has ended
                    _stat.toDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                    // Record this stat event in the statLog object
                    _statLog.RecordStat(_stat);
                }
            }
            catch
            {
                System.Diagnostics.Trace.WriteLine(new LogMessage("Region - Clear", "Error closing off stat record"), LogType.Error.ToString());
            }
        }

        /// <summary>
        /// Performs the disposal.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _media.Dispose();
                    _media = null;

                    System.Diagnostics.Debug.WriteLine("Media Disposed by Region", "Region - Dispose");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                    System.Diagnostics.Debug.WriteLine("There was no media to dispose", "Region - Dispose");
                }
                finally
                {
                    if (_media != null) _media = null;
                }
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// The options specific to a region
    /// </summary>
    struct RegionOptions
    {
        public double scaleFactor;
        public int width;
        public int height;
        public int top;
        public int left;

        public int backgroundLeft;
        public int backgroundTop;

        public string type;
        public string uri;
        public int duration;

        //xml
        public XmlNodeList mediaNodes;

        //rss options
        public string direction;
        public string text;
        public string documentTemplate;
        public string copyrightNotice;
        public string javaScript;
        public int updateInterval;
        public int scrollSpeed;
        
        //The identification for this region
        public string mediaid;
        public int layoutId;
        public string regionId;
        public int scheduleId;
       
        //general options
        public string backgroundImage;
        public string backgroundColor;

        public MediaDictionary Dictionary;

        public override string ToString()
        {
            return String.Format("({0},{1},{2},{3},{4},{5})", width, height, top, left, type, uri);
        }
    }
}
