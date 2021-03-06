﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Activation;
using System.ServiceModel.Web;
using System.Xml.Linq;
using Tridion.ContentManager;
using Tridion.ContentManager.CoreService.Client;
using Tridion.ExternalContentLibrary.V2;

namespace EclImport.Model.Services
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple)]
    [AspNetCompatibilityRequirements(RequirementsMode = AspNetCompatibilityRequirementsMode.Required)]
    [ServiceContract(Namespace = "EclImport.Model.Services")]
    public class ImportService
    {
        private string _username;
        private string _folderUri;
        private string _schemaUri;
        private IContentLibraryContext _eclContentLibraryContext;

        [OperationContract]
        [WebInvoke(Method = "POST", RequestFormat = WebMessageFormat.Json, ResponseFormat = WebMessageFormat.Json)]
        public string ImportItem(string eclOrTcmUri, string folderUri, string schemaUri)
        {
            _folderUri = folderUri;
            _schemaUri = schemaUri;
            _username = ServiceSecurityContext.Current.WindowsIdentity.Name;

            // create new tcm session for current user, so we can create an ecl session to get the ecl item and read its content
            using (Session tcmSession = new Session(_username))
            using (IEclSession eclSession = SessionFactory.CreateEclSession(tcmSession))
            {
                IEclUri eclUri = eclOrTcmUri.StartsWith("tcm:") ? eclSession.TryGetEclUriFromTcmUri(eclOrTcmUri) : eclSession.HostServices.CreateEclUri(eclOrTcmUri);
                if (eclUri != null)
                {
                    using (_eclContentLibraryContext = eclSession.GetContentLibrary(eclUri))
                    {
                        if (eclUri.ItemType == EclItemTypes.Folder)
                        {
                            // loop over all ECL items in this folder
                            IFolderContent items = _eclContentLibraryContext.GetFolderContent(eclUri, 0, EclItemTypes.File);
                            IList<string> ids = items.ChildItems.Select(item => ImportSingleItem(item.Id)).ToList();
                            return string.Join(",", ids.ToArray());
                        }

                        return ImportSingleItem(eclUri);
                    }
                }
            }

            throw new FaultException(string.Format("{0} does not exist.", eclOrTcmUri));
        }

        private string ImportSingleItem(IEclUri eclUri)
        {
            string id = "tcm:0-0-0";
            IContentLibraryMultimediaItem eclItem = (IContentLibraryMultimediaItem)_eclContentLibraryContext.GetItem(eclUri);
            string extension = eclItem.Filename.Substring(eclItem.Filename.LastIndexOf('.') + 1);
            MemoryStream ms = null;
            string tempPath;
            try
            {
                // create some template attributes
                IList<ITemplateAttribute> attributes = CreateTemplateAttributes(eclItem);

                // determine if item has content or is available online
                string publishedPath = eclItem.GetDirectLinkToPublished(attributes);
                if (string.IsNullOrEmpty(publishedPath))
                {
                    // we can directly get the content 
                    IContentResult content = eclItem.GetContent(attributes);
                    ms = new MemoryStream();
                    content.Stream.CopyTo(ms);
                    ms.Position = 0;
                }
                else
                {
                    // read the content from the publish path
                    using (WebClient webClient = new WebClient())
                    {
                        byte[] thumbnailData = webClient.DownloadData(publishedPath);
                        ms = new MemoryStream(thumbnailData, false);
                    }
                }

                // upload binary (using netTcp binding as configured in SDL Tridion, because this Model extension is running inside the UI) 
                using (StreamUploadClient suClient = new StreamUploadClient("streamUpload_netTcp_2012"))
                {
                    tempPath = suClient.UploadBinaryContent(eclItem.Filename, ms);
                }
            }
            finally
            {
                if (ms != null)
                {
                    ms.Dispose();
                }
            }

            // create tcm item
            var mmComponent = new ComponentData
            {
                Id = id,
                Title = eclItem.Title,
                Schema = new LinkToSchemaData { IdRef = _schemaUri },
                LocationInfo = new LocationInfo { OrganizationalItem = new LinkToOrganizationalItemData { IdRef = _folderUri } }
            };

            // put binary data in tcm item (using netTcp binding as configured in SDL Tridion, because this Model extension is running inside the UI) 
            using (SessionAwareCoreServiceClient client = new SessionAwareCoreServiceClient("netTcp_2012"))
            {
                // impersonate with current user
                client.Impersonate(_username);

                // set metadata
                var schemaFields = client.ReadSchemaFields(_schemaUri, true, new ReadOptions());
                if (schemaFields.MetadataFields.Any())
                {
                    var fields = Fields.ForMetadataOf(schemaFields, mmComponent);
                    if (!string.IsNullOrEmpty(eclItem.MetadataXml))
                    {
                        XNamespace ns = GetNamespace(eclItem.MetadataXml);
                        XDocument metadata = XDocument.Parse(eclItem.MetadataXml);
                        var children = metadata.Element(ns + "Metadata").Descendants();
                        for (int i = 0; i < children.Count(); i++)
                        {
                            fields.AddFieldElement(new ItemFieldDefinitionData { Name = "data" });
                            var embeddedFields = fields["data"].GetSubFields(i);
                            embeddedFields.AddFieldElement(new ItemFieldDefinitionData { Name = "key" });
                            embeddedFields.AddFieldElement(new ItemFieldDefinitionData { Name = "value" });
                            embeddedFields["key"].Value = children.ElementAt(i).Name.LocalName;
                            embeddedFields["value"].Value = children.ElementAt(i).Value;
                        }
                    }
                    mmComponent.Metadata = fields.ToString();
                }

                // find multimedia type
                var list = client.GetSystemWideList(new MultimediaTypesFilterData());
                var multimediaType = list.OfType<MultimediaTypeData>().Single(mt => mt.FileExtensions.Contains(extension));

                // set BinaryContent of a component
                mmComponent.BinaryContent = new BinaryContentData
                {
                    UploadFromFile = tempPath,
                    Filename = eclItem.Filename,
                    MultimediaType = new LinkToMultimediaTypeData { IdRef = multimediaType.Id }
                };

                // create (and save) component
                ComponentData data = (ComponentData)client.Create(mmComponent, new ReadOptions());
                id = data.Id;
            }

            //string result = string.Format("created {0}, from {1}, in {2}, using {3}, for {4}", id, eclUri, _folderUri, _schemaUri, _username);
            return id;
        }

        private static IList<ITemplateAttribute> CreateTemplateAttributes(IContentLibraryMultimediaItem eclItem)
        {
            IList<ITemplateAttribute> attributes = new List<ITemplateAttribute>();
            if (eclItem.Width != null && eclItem.Width > 0)
            {
                attributes.Add(new NodeKeyValuePair(new KeyValuePair<string, string>("width", eclItem.Width.ToString())));
            }
            if (eclItem.Height != null && eclItem.Height > 0)
            {
                attributes.Add(new NodeKeyValuePair(new KeyValuePair<string, string>("height", eclItem.Height.ToString())));
            }
            attributes.Add(new NodeKeyValuePair(new KeyValuePair<string, string>("src", eclItem.Id.ToString())));

            return attributes;
        }

        private static string GetNamespace(string xml)
        {
            // ecl metadata string looks like <Metadata xmlns="..">..</Metadata>
            const string nsAttribute = "xmlns=\"";
            int start = xml.IndexOf(nsAttribute);
            int end = xml.IndexOf('\"', start + nsAttribute.Length);
            return xml.Substring(start + nsAttribute.Length, end - start - nsAttribute.Length);
        }
    }
}
