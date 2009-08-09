<?xml version="1.0" encoding="ISO-8859-1"?>
<xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:x="urn:schemas-microsoft-com:office:excel" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet" xmlns:html="http://www.w3.org/TR/REC-html40" version="1.0">
<!-- 
	www.kinovea.org
	
	This stylesheet formats a .kva file to MS-EXCEL XML spreadsheet.
	You can use it to view the content of the .kva  in MS-EXCEL.
-->

  <xsl:output method="xml" encoding="UTF-8" indent="yes"/>
  <xsl:template match="/">
    
    <xsl:text disable-output-escaping="yes">&lt;?mso-application progid="Excel.Sheet"?&gt;</xsl:text>
    
    <Workbook>      
      
      <DocumentProperties xmlns="urn:schemas-microsoft-com:office:office">
        <Title>
          <xsl:value-of select="KinoveaVideoAnalysis/OriginalFilename"/>  
        </Title>
      </DocumentProperties>      
      
      <ExcelWorkbook xmlns="urn:schemas-microsoft-com:office:excel">
        <WindowHeight>12270</WindowHeight>
        <WindowWidth>14955</WindowWidth>
        <WindowTopX>720</WindowTopX>
        <WindowTopY>315</WindowTopY>
        <ProtectStructure>False</ProtectStructure>
        <ProtectWindows>False</ProtectWindows>
      </ExcelWorkbook>      
      
      <Styles>
        <!-- Style for Keyframes header -->
        <Style ss:ID="s21">
          <Borders>
            <Border ss:Position="Bottom" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Left" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Right" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Top" ss:LineStyle="Continuous" ss:Weight="1"/>
          </Borders>
          <Interior ss:Color="#ccffcc" ss:Pattern="Solid"/>
        </Style>
        <!-- Style for Tracks header -->
        <Style ss:ID="s22">
          <Borders>
            <Border ss:Position="Bottom" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Left" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Right" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Top" ss:LineStyle="Continuous" ss:Weight="1"/>
          </Borders>
          <Interior ss:Color="#99ccff" ss:Pattern="Solid"/>
        </Style>
        <!-- Style for Chronos header -->
        <Style ss:ID="s23">
          <Borders>
            <Border ss:Position="Bottom" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Left" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Right" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Top" ss:LineStyle="Continuous" ss:Weight="1"/>
          </Borders>
          <Interior ss:Color="#cc99ff" ss:Pattern="Solid"/>
        </Style>
        <!-- Style for data values -->
        <Style ss:ID="s24">
          <Borders>
            <Border ss:Position="Bottom" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Left" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Right" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Top" ss:LineStyle="Continuous" ss:Weight="1"/>
          </Borders>
          <Interior ss:Color="#ffffff" ss:Pattern="Solid"/>
        </Style>
        <!-- Style for x,y headers -->
        <Style ss:ID="s25">
          <Alignment ss:Horizontal="Center" ss:Vertical="Bottom"/>
          <Borders>
            <Border ss:Position="Bottom" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Left" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Right" ss:LineStyle="Continuous" ss:Weight="1"/>
            <Border ss:Position="Top" ss:LineStyle="Continuous" ss:Weight="1"/>
          </Borders>
          <Interior ss:Color="#ffffff" ss:Pattern="Solid"/>
        </Style>
      </Styles>
      
      <Worksheet>
        <xsl:attribute name="ss:Name">
          <xsl:value-of select="KinoveaVideoAnalysis/OriginalFilename"/>
        </xsl:attribute>
        <Table x:FullColumns="1" x:FullRows="1" ss:DefaultColumnWidth="60">
          <xsl:apply-templates select="//Keyframes"/>
          <xsl:apply-templates select="//Tracks"/>
          <xsl:apply-templates select="//Chronos"/>
        </Table>
      </Worksheet>
      
    </Workbook>
    
  </xsl:template>
  
  <xsl:template match="Keyframes">
    <Row>
      <Cell ss:StyleID="s21">
        <Data ss:Type="String">Key Images</Data>
      </Cell>
    </Row>
    <xsl:for-each select="Keyframe">
      <Row>
        <Cell ss:StyleID="s24">
          <Data ss:Type="String">Title :</Data>
        </Cell>
        <Cell ss:StyleID="s24">
          <Data ss:Type="String">
            <xsl:value-of select="Title"/>
          </Data>
        </Cell>
      </Row>
    </xsl:for-each>
  </xsl:template>
  <xsl:template match="Tracks">
    <Row>
      <Cell>
        <Data ss:Type="String"/>
      </Cell>
    </Row>
    <xsl:for-each select="Track">
      <Row>
        <Cell>
          <Data ss:Type="String"/>
        </Cell>
      </Row>
      <!-- Track table -->
      <Row>
        <Cell ss:StyleID="s22">
          <Data ss:Type="String">Trajectory</Data>
        </Cell>
      </Row>
      <Row>
        <Cell ss:StyleID="s24">
          <Data ss:Type="String">Label :</Data>
        </Cell>
        <Cell ss:StyleID="s24">
          <Data ss:Type="String">
            <xsl:value-of select="Label/Text"/>
          </Data>
        </Cell>
      </Row>
      <Row>
        <Cell ss:StyleID="s24">
          <Data ss:Type="String">Coords (x,y:<xsl:value-of select="TrackPositionList/@UserUnitLength"/>; t:time)</Data>
        </Cell>
      </Row>
      <Row>
        <Cell ss:StyleID="s25">
          <Data ss:Type="String">x</Data>
        </Cell>
        <Cell ss:StyleID="s25">
          <Data ss:Type="String">y</Data>
        </Cell>
        <Cell ss:StyleID="s25">
          <Data ss:Type="String">t</Data>
        </Cell>
      </Row>
      <xsl:for-each select="TrackPositionList/TrackPosition">
        <Row>
          <!-- We need to replace the comma with dots. ie. Excel will only accept "-0.32", not "-0,32". -->
          <Cell ss:StyleID="s24"><Data ss:Type="Number"><xsl:value-of select="concat(substring-before(@UserX,','), '.', substring-after(@UserX,','))"/></Data></Cell>          
          <Cell ss:StyleID="s24"><Data ss:Type="Number"><xsl:value-of select="concat(substring-before(@UserY,','), '.', substring-after(@UserY,','))"/></Data></Cell>                    
          <Cell ss:StyleID="s24"><Data ss:Type="String"><xsl:value-of select="@UserTime"/></Data></Cell>
        </Row>
      </xsl:for-each>
    </xsl:for-each>
  </xsl:template>
  <xsl:template match="Chronos">
    <Row>
      <Cell>
        <Data ss:Type="String"/>
      </Cell>
    </Row>
    <xsl:for-each select="Chrono">
      <Row>
        <Cell>
          <Data ss:Type="String"/>
        </Cell>
      </Row>
<!-- Chrono table -->
      <Row>
        <Cell ss:StyleID="s23">
          <Data ss:Type="String">Chrono</Data>
        </Cell>
      </Row>
      <Row>
        <Cell ss:StyleID="s24">
          <Data ss:Type="String">Label :</Data>
        </Cell>
        <Cell ss:StyleID="s24">
          <Data ss:Type="String">
            <xsl:value-of select="Label/Text"/>
          </Data>
        </Cell>
      </Row>
      <Row>
        <Cell ss:StyleID="s24">
          <Data ss:Type="String">Duration</Data>
        </Cell>
        <Cell ss:StyleID="s24">
          <Data ss:Type="String">
            <xsl:value-of select="Values/UserDuration"/>
          </Data>
        </Cell>
      </Row>
    </xsl:for-each>
  </xsl:template>
  <xsl:template name="tokenize">
    <xsl:param name="inputString"/>
    <xsl:param name="separator" select="';'"/>
    <!-- Split next value from the rest -->
    <xsl:variable name="token" select="substring-before($inputString, $separator)"/>
    <xsl:variable name="nextToken" select="substring-after($inputString, $separator)"/>
    
    <xsl:choose>
      <xsl:when test="$token">
        <Cell>
          <xsl:attribute name="ss:StyleID">
            <xsl:value-of select="'s24'"/>
          </xsl:attribute>
          <Data ss:Type="Number">
            <xsl:value-of select="$token"/>
          </Data>
        </Cell>
        <!-- recursive call to tokenize for the rest -->
        <xsl:if test="$nextToken">
          <xsl:call-template name="tokenize">
            <xsl:with-param name="inputString" select="$nextToken"/>
            <xsl:with-param name="separator" select="$separator"/>
          </xsl:call-template>
        </xsl:if>
      </xsl:when>
      <xsl:otherwise>
        <Cell>
          <xsl:attribute name="ss:StyleID">
            <xsl:value-of select="'s24'"/>
          </xsl:attribute>
          <Data ss:Type="Number">
            <xsl:value-of select="$inputString"/>
          </Data>
        </Cell>
      </xsl:otherwise>
    </xsl:choose>
  </xsl:template>
</xsl:stylesheet>
